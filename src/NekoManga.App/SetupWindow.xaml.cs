using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using NekoManga.Core.Runtime;
using Component = NekoManga.Core.Runtime.Component;

namespace NekoManga.App;

public class ComponentRow : INotifyPropertyChanged
{
    public required Component Component { get; init; }
    public string Name => Component.ForComfy ? Component.DisplayName + "  · 채색용(선택)" : Component.DisplayName;

    private string _status = "대기";
    public string Status { get => _status; set { _status = value; Notify(nameof(Status)); } }

    private double _percent;
    public double Percent { get => _percent; set { _percent = value; Notify(nameof(Percent)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string n) => PropertyChanged?.Invoke(this, new(n));
}

public partial class SetupWindow
{
    private readonly ObservableCollection<ComponentRow> _rows = [];
    private ComponentInstaller _installer = new(App.Settings);
    private bool _running;
    private bool _demo;            // --demo-setup: UI 확인용 — 설정 파일을 건드리지 않는다
    private bool _syncingDrive;

    public SetupWindow(bool demo = false)
    {
        InitializeComponent();
        _demo = demo;
        TxtNote.Text = ComponentCatalog.FirstRunNote;
        CompList.ItemsSource = _rows;
        PopulateDrives();
        RefreshList();
    }

    /// <summary>드라이브 문자 → SSD/HDD 종류. WMI(저장소 네임스페이스)로 파티션→물리디스크를 잇는다.
    /// 조회 실패(권한·가상 디스크 등)는 빈 문자열 — 표시만 생략된다.</summary>
    private static Dictionary<string, string> QueryDriveMedia()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var scope = new System.Management.ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            var diskMedia = new Dictionary<string, string>(); // DeviceId → 종류
            using (var disks = new System.Management.ManagementObjectSearcher(scope,
                new System.Management.ObjectQuery("SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk")))
                foreach (var d in disks.Get())
                {
                    var id = d["DeviceId"]?.ToString() ?? "";
                    var media = Convert.ToInt32(d["MediaType"] ?? 0);   // 3=HDD, 4=SSD
                    var bus = Convert.ToInt32(d["BusType"] ?? 0);       // 17=NVMe
                    diskMedia[id] = media switch
                    {
                        4 => bus == 17 ? "NVMe SSD" : "SSD",
                        3 => "HDD",
                        _ => "",
                    };
                }

            using var parts = new System.Management.ManagementObjectSearcher(scope,
                new System.Management.ObjectQuery("SELECT DriveLetter, DiskNumber FROM MSFT_Partition"));
            foreach (var p in parts.Get())
            {
                var letter = p["DriveLetter"]?.ToString();
                if (string.IsNullOrEmpty(letter) || letter == "\0") continue;
                var disk = p["DiskNumber"]?.ToString() ?? "";
                if (diskMedia.TryGetValue(disk, out var kind) && kind.Length > 0)
                    map[letter + ":\\"] = kind;
            }
        }
        catch { }
        return map;
    }

    /// <summary>고정 드라이브 목록을 여유 공간·SSD/HDD 종류와 함께 채운다 — 요즘은 부팅 SSD(C:)를
    /// 가볍게 두고 보조 드라이브에 큰 파일을 모으는 구성이 흔해서, 드라이브 단위 선택이 기본이다.
    /// 추천은 '용량이 충분한 SSD 중 여유 최대' — 모델 로딩이 잦아 HDD는 눈에 띄게 느리다.</summary>
    private void PopulateDrives()
    {
        _syncingDrive = true;
        CmbDrive.Items.Clear();
        var media = QueryDriveMedia();
        long neededBytes = ComponentCatalog.All.Sum(c => c.ApproxBytes);

        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .OrderByDescending(d => d.AvailableFreeSpace)
            .ToList();

        bool IsSsd(DriveInfo d) =>
            media.TryGetValue(d.RootDirectory.FullName, out var k) && k.Contains("SSD");
        var best = drives.Where(d => IsSsd(d) && d.AvailableFreeSpace > neededBytes * 1.1)
                         .OrderByDescending(d => d.AvailableFreeSpace)
                         .FirstOrDefault()
                   ?? drives.FirstOrDefault();

        foreach (var d in drives)
        {
            double freeGb = d.AvailableFreeSpace / 1024.0 / 1024 / 1024;
            media.TryGetValue(d.RootDirectory.FullName, out var kind);
            CmbDrive.Items.Add(new ComboBoxItem
            {
                Content = $"{d.Name}  —  여유 {freeGb:0}GB" +
                          (string.IsNullOrEmpty(kind) ? "" : $" · {kind}") +
                          (ReferenceEquals(d, best) ? "  (권장)" : ""),
                Tag = d.RootDirectory.FullName,
            });
        }
        CmbDrive.Items.Add(new ComboBoxItem { Content = "직접 지정한 폴더", Tag = "" });

        // 현재 RuntimeRoot가 어느 드라이브인지 골라 둔다
        var root = Path.GetPathRoot(App.Settings.RuntimeRoot) ?? "";
        var match = CmbDrive.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(it => (string)it.Tag! == root && App.Settings.RuntimeRoot ==
                Path.Combine(root, "NekoManga-runtime"));
        CmbDrive.SelectedItem = match ?? CmbDrive.Items[^1];
        _syncingDrive = false;
        UpdateLocationInfo();
    }

    private void CmbDrive_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncingDrive || _running || CmbDrive.SelectedItem is not ComboBoxItem it) return;
        var driveRoot = (string)it.Tag!;
        if (driveRoot.Length == 0) { BtnPickRoot_Click(sender, new RoutedEventArgs()); return; }
        SetRoot(Path.Combine(driveRoot, "NekoManga-runtime"));
    }

    private void SetRoot(string path)
    {
        App.Settings.RuntimeRoot = path;
        if (!_demo) App.Settings.Save();
        _installer = new ComponentInstaller(App.Settings);
        RefreshList();
        UpdateLocationInfo();
    }

    /// <summary>선택 위치 전체 경로 + 필요/여유 용량을 보여준다. 공간이 부족하면 경고색.</summary>
    private void UpdateLocationInfo()
    {
        TxtRoot.Text = "설치 폴더: " + App.Settings.RuntimeRoot;
        try
        {
            long needed = ComponentCatalog.All
                .Where(c => !_installer.IsInstalled(c))
                .Sum(c => c.ApproxBytes);
            double needGb = needed / 1024.0 / 1024 / 1024;
            var drive = new DriveInfo(Path.GetPathRoot(App.Settings.RuntimeRoot)!);
            double freeGb = drive.AvailableFreeSpace / 1024.0 / 1024 / 1024;
            bool ok = freeGb > needGb * 1.1;
            TxtSpace.Text = needed == 0
                ? $"이 위치에 모두 설치돼 있어요 · 드라이브 여유 {freeGb:0}GB"
                : $"필요 공간 약 {needGb:0.#}GB  /  드라이브 여유 {freeGb:0}GB" + (ok ? "" : "  ⚠ 공간이 부족해요 — 다른 드라이브를 골라주세요");
            TxtSpace.Foreground = ok
                ? (System.Windows.Media.Brush)FindResource("NekoText")
                : System.Windows.Media.Brushes.IndianRed;
            BtnInstall.IsEnabled = BtnInstall.IsEnabled && ok;
        }
        catch { TxtSpace.Text = ""; }
    }

    private void RefreshList()
    {
        _rows.Clear();
        foreach (var c in ComponentCatalog.All)
        {
            var installed = _installer.IsInstalled(c);
            _rows.Add(new ComponentRow
            {
                Component = c,
                Status = installed ? "설치됨 ✓" : "필요",
                Percent = installed ? 100 : 0,
            });
        }
        BtnInstall.IsEnabled = _rows.Any(r => r.Status == "필요");
        if (!BtnInstall.IsEnabled) BtnInstall.Content = "모두 준비됨 ✓";
    }

    private void BtnPickRoot_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "구성요소 설치 위치 (여유 공간 25GB 이상 권장)",
            SelectedPath = App.Settings.RuntimeRoot,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            SetRoot(dlg.SelectedPath);
    }

    private async void BtnInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _running = true;
        BtnInstall.IsEnabled = false;
        BtnSkip.Content = "숨기기";

        var progress = new Progress<InstallProgress>(p =>
        {
            var row = _rows.FirstOrDefault(r => r.Component.Id == p.ComponentId);
            if (row == null) return;
            row.Status = p.Message;
            row.Percent = p.Percent;
        });

        try
        {
            foreach (var row in _rows.Where(r => r.Status == "필요").ToList())
            {
                row.Status = "내려받는 중";
                await _installer.InstallAsync(row.Component, progress, CancellationToken.None);
                row.Status = "설치됨 ✓";
                row.Percent = 100;
            }
            TxtNote.Text = "준비 끝! 이제 바로 쓸 수 있어요 (ﾉ´ヮ`)ﾉ*:･ﾟ✧";
            BtnSkip.Content = "시작하기";
            BtnInstall.Content = "모두 준비됨 ✓";
        }
        catch (Exception ex)
        {
            TxtNote.Text = "문제가 생겼어요: " + ex.Message;
            BtnInstall.IsEnabled = true;
        }
        finally { _running = false; }
    }

    private void BtnSkip_Click(object sender, RoutedEventArgs e) => Close();
}
