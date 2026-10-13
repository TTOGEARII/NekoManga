using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
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

    public SetupWindow()
    {
        InitializeComponent();
        TxtRoot.Text = App.Settings.RuntimeRoot;
        TxtNote.Text = ComponentCatalog.FirstRunNote;
        CompList.ItemsSource = _rows;
        RefreshList();
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
            Description = "구성요소 설치 위치 (여유 공간 15GB 이상 권장)",
            SelectedPath = App.Settings.RuntimeRoot,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            App.Settings.RuntimeRoot = dlg.SelectedPath;
            App.Settings.Save();
            TxtRoot.Text = dlg.SelectedPath;
            _installer = new ComponentInstaller(App.Settings);
            RefreshList();
        }
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
