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
        Loaded += async (_, _) => await RefreshPcAsync();
    }

    /// <summary>점검 패널과 맞춤 설정을 함께 새로 고친다 — nvidia-smi는 한 번만 묻고 둘이 같은 답을 쓴다.
    /// 멈춘 nvidia-smi는 부를 때마다 8초를 기다리게 한다: 2026-11-11까지는 창이 열릴 때 점검 1번 + 맞춤 설정 2번을 따로 불렀다.
    /// 설치할 것이 없으면 두 패널 다 숨으니 묻지 않는다.</summary>
    private async Task RefreshPcAsync()
    {
        var smi = _rows.Any(r => r.Status == "필요") ? Task.Run(SystemCheck.QueryNvidiaAll) : null;
        try { await CheckRequirementsAsync(smi); } catch { /* 점검 실패는 설치를 막지 않는다 */ }
        try { await RefreshTuneAsync(smi); } catch { }
    }

    // ── PC 사양 맞춤 설정 (2026-10-21) — 설치 대상이 있을 때만 보여 주고 [준비 시작!] 때 적용한다 ──
    private AutoTune.Plan? _tune;

    /// <summary>smi = 같은 흐름에서 이미 묻기 시작한 nvidia-smi 결과(없으면 여기서 묻는다).</summary>
    private async Task RefreshTuneAsync(Task<IReadOnlyList<SystemCheck.Gpu>>? smi = null)
    {
        if (!_rows.Any(r => r.Status == "필요")) { PnlTune.Visibility = Visibility.Collapsed; _tune = null; return; }
        var s = App.Settings;
        _tune = await Task.Run(async () => smi != null ? AutoTune.Build(s, await smi) : AutoTune.Build(s));
        TxtTuneHw.Text = "감지: " + _tune.HardwareLine;
        TxtTune.Text = _tune.Summary;
        PnlTune.Visibility = Visibility.Visible;
    }

    /// <summary>드라이브 문자 → SSD/HDD 종류. 볼륨 핸들에 IOCTL_STORAGE_QUERY_PROPERTY —
    /// 탐색 지연(seek penalty) 없음 = SSD, 버스 17 = NVMe. 관리자 권한 불필요.
    /// 조회 실패(가상 디스크 등)는 그 드라이브만 생략된다.
    /// WMI(System.Management)를 쓰지 않는 이유: 로컬 검증 배포는 DLL 3개만 덮어써서
    /// System.Management.dll이 설치본에 없고, 어셈블리 로드 실패는 try 진입 전 JIT에서
    /// 터져 catch로도 못 잡는다 — 구성요소 창을 열다 앱이 통째로 죽은 실사고.</summary>
    private static Dictionary<string, string> QueryDriveMedia()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
            try
            {
                char letter = d.Name[0];
                using var h = DriveProbe.OpenVolume(letter);
                if (h.IsInvalid) continue;
                bool? ssd = DriveProbe.HasNoSeekPenalty(h);
                if (ssd == null) continue;
                map[d.RootDirectory.FullName] = ssd.Value
                    ? (DriveProbe.BusType(h) == 17 ? "NVMe SSD" : "SSD")
                    : "HDD";
            }
            catch { }
        }
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
        _ = RefreshTuneSafeAsync();   // 설치 대상이 바뀌면 맞춤 패널도 다시 (이미 설치된 위치면 숨김)
    }

    private async Task RefreshTuneSafeAsync() { try { await RefreshTuneAsync(); } catch { } }

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

    // ── ComfyUI 묶음 전제 조건 점검 (2026-10-20: 다른 PC에서 옛 드라이버로 torch c10.dll 1114 크래시 → 설치 전에 안내) ──
    private SystemCheck.Report? _req;

    /// <summary>ComfyUI 묶음이 설치 대상에 있으면 PC를 점검해 패널에 보여 준다. 막히는 항목이 있으면 true.
    /// smi = 같은 흐름에서 이미 묻기 시작한 nvidia-smi 결과(없으면 여기서 묻는다). 붙잡아 둔 옛 답을 넘기지 않는다 —
    /// [다시 확인]·[준비 시작!]은 드라이버를 올린 뒤일 수 있어 부르는 쪽이 그때마다 새로 물어서 넘긴다.</summary>
    private async Task<bool> CheckRequirementsAsync(Task<IReadOnlyList<SystemCheck.Gpu>>? smi = null)
    {
        bool needComfy = _rows.Any(r => r.Component.ForComfy && r.Status == "필요");
        if (!needComfy) { PnlReq.Visibility = Visibility.Collapsed; _req = null; return false; }
        TxtReqTitle.Text = "PC 점검 중..."; PnlReq.Visibility = Visibility.Visible;
        if (CmbGpu.SelectedItem == null)
            foreach (ComboBoxItem it in CmbGpu.Items) if ((string)it.Tag == App.Settings.GpuVendor) { CmbGpu.SelectedItem = it; break; }
        var vendor = App.Settings.GpuVendor;
        _req = await Task.Run(async () => smi != null ? SystemCheck.CheckForComfy(vendor, await smi) : SystemCheck.CheckForComfy(vendor));
        var r = _req;
        BtnReqDriver.Visibility = r.Items.Any(i => !i.Ok && (i.FixUrl == SystemCheck.DriverUrl || i.FixUrl == SystemCheck.AmdDriverUrl)) ? Visibility.Visible : Visibility.Collapsed;
        BtnReqDriver.Content = r.Items.Any(i => !i.Ok && i.FixUrl == SystemCheck.AmdDriverUrl) ? "⬇ AMD 드라이버 받기" : "⬇ NVIDIA 드라이버 받기";
        BtnReqVc.Visibility = r.Items.Any(i => !i.Ok && i.FixUrl == SystemCheck.VcRedistUrl) ? Visibility.Visible : Visibility.Collapsed;
        if (r.HasBlocking)
        {
            TxtReqTitle.Text = "✖ 채색 엔진(ComfyUI)을 쓰려면 먼저 해야 할 일이 있어요";
            TxtReq.Text = r.Summary + "\n" + r.BlockedNote;   // 너무 오래된 카드에는 "위 항목을 해결한 뒤…"를 붙이지 않는다(Report.BlockedNote)
        }
        else if (r.HasWarning)
        {
            TxtReqTitle.Text = "⚠ PC 점검 — 설치는 되지만 참고하세요";
            TxtReq.Text = r.Summary;
        }
        else
        {
            TxtReqTitle.Text = "✔ PC 점검 통과";
            TxtReq.Text = string.Join(" · ", r.Items.Select(i => i.Title + " " + i.Detail.Split(' ')[0]));
        }
        return r.HasBlocking;
    }

    private void BtnReqDriver_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            _req != null && _req.Items.Any(i => !i.Ok && i.FixUrl == SystemCheck.AmdDriverUrl) ? SystemCheck.AmdDriverUrl : SystemCheck.DriverUrl) { UseShellExecute = true });

    private async void CmbGpu_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CmbGpu.SelectedItem is not ComboBoxItem it || _running) return;
        var tag = (string)it.Tag;
        if (tag == App.Settings.GpuVendor) return;
        App.Settings.GpuVendor = tag; if (!_demo) App.Settings.Save();
        await RefreshPcAsync();
    }
    private void BtnReqVc_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SystemCheck.VcRedistUrl) { UseShellExecute = true });
    // [다시 확인]은 점검과 맞춤 설정을 같이 새로 고친다(nvidia-smi는 한 번, 둘이 같은 답) — 2026-11-11 검수: 점검만 새로 해서, 드라이버를 올리거나 카드를 바꾼 뒤에도
    // 맞춤 설정 패널에는 창을 열 때의 계획("자동 채색: 켬 → 끔")이 남았고 [준비 시작!]이 그것을 적용했다
    private async void BtnReqRecheck_Click(object sender, RoutedEventArgs e) { if (!_running) await RefreshPcAsync(); }

    private async void BtnInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _running = true;
        BtnInstall.IsEnabled = false;
        CmbGpu.IsEnabled = false;   // 받는 도중 종류를 바꾸면 저장도 안 되고 콤보만 달라져 혼란 — 끝나면 다시 켠다
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
            // 엔진 묶음 전제 조건이 안 맞으면 그 묶음은 건너뛰고(내장 엔진은 그대로 설치) 안내만 남긴다.
            // nvidia-smi는 여기서 한 번만 새로 묻고(드라이버를 올린 뒤일 수 있다) 점검과 설치기의 묶음 고르기가 그 답을 같이 쓴다 —
            // 2026-11-11까지는 설치기가 ComfyUI 포터블을 받을 때 한 번 더 물었다(멈춘 nvidia-smi면 8초 더, 점검이 본 카드와 받는 묶음이 어긋날 수도)
            var smi = _rows.Any(r => r.Component.ForComfy && r.Status == "필요") ? Task.Run(SystemCheck.QueryNvidiaAll) : null;
            bool blocked = await CheckRequirementsAsync(smi);
            string? detectedVendor = smi != null ? await Task.Run(async () => SystemCheck.DetectVendor(await smi).Vendor) : null;
            if (blocked)
                foreach (var row in _rows.Where(r => r.Component.ForComfy && r.Status == "필요")) row.Status = "조건 미충족 — 위 안내 참고";

            // PC 사양 맞춤 설정 적용 (체크돼 있을 때만; demo는 저장 안 함)
            if (ChkTune.IsChecked == true && _tune is { Changes.Count: > 0 })
            {
                AutoTune.Apply(App.Settings, _tune);
                if (!_demo) App.Settings.Save();
                TxtTune.Text = "적용했어요:\n" + _tune.Summary;
            }

            foreach (var row in _rows.Where(r => r.Status == "필요").ToList())
            {
                row.Status = "내려받는 중";
                await _installer.InstallAsync(row.Component, progress, CancellationToken.None, detectedVendor);
                row.Status = "설치됨 ✓";
                row.Percent = 100;
            }
            bool leftover = _rows.Any(r => r.Status.StartsWith("조건 미충족"));
            // 너무 오래된 카드에는 "위 조건을 해결한 뒤…"라고 하지 않는다 — 해결할 조건이 없다(Report.LeftoverNote)
            TxtNote.Text = leftover ? (_req ?? new SystemCheck.Report([])).LeftoverNote
                                    : "준비 끝! 이제 바로 쓸 수 있어요 (ﾉ´ヮ`)ﾉ*:･ﾟ✧";
            BtnSkip.Content = "시작하기";
            if (leftover) { foreach (var row in _rows.Where(r => r.Status.StartsWith("조건 미충족"))) row.Status = "필요"; BtnInstall.IsEnabled = true; }
            else BtnInstall.Content = "모두 준비됨 ✓";
        }
        catch (Exception ex)
        {
            TxtNote.Text = "문제가 생겼어요: " + ex.Message;
            BtnInstall.IsEnabled = true;
        }
        finally { _running = false; CmbGpu.IsEnabled = true; }
    }

    private void BtnSkip_Click(object sender, RoutedEventArgs e) => Close();
}
