using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using NekoManga.Core.Pipeline;
using Velopack;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using System.Windows.Media.Imaging;
using DragEventArgs = System.Windows.DragEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace NekoManga.App;

public class FileRow : INotifyPropertyChanged
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);

    private string _stage = "대기";
    public string Stage { get => _stage; set { _stage = value; Notify(nameof(Stage)); } }

    private double _percent;
    public double Percent { get => _percent; set { _percent = value; Notify(nameof(Percent)); } }

    /// <summary>목록에 띄우는 작은 그림. 결과물이 있으면 결과물을, 없으면 원본을 보여준다.</summary>
    private BitmapSource? _thumb;
    public BitmapSource? Thumb { get => _thumb; set { _thumb = value; Notify(nameof(Thumb)); } }

    /// <summary>미리보기·수정 대상 경로 (결과물이 나오면 그쪽을 가리킨다).</summary>
    private string? _viewPath;
    public string ViewPath { get => _viewPath ?? Path; set { _viewPath = value; Notify(nameof(ViewPath)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string n) => PropertyChanged?.Invoke(this, new(n));
}

public partial class MainWindow
{
    /// <summary>OpenCV(ImRead)가 읽을 수 있는 포맷. 식자·검열 단계도 전부 OpenCV를 쓰므로 동일 목록이다.</summary>
    private static readonly string[] ImageExts =
        [".png", ".jpg", ".jpeg", ".jpe", ".webp", ".bmp", ".tif", ".tiff", ".jp2", ".ppm", ".pgm", ".pbm", ".pnm", ".avif"];

    private readonly ObservableCollection<FileRow> _rows = [];
    private readonly UpdateService _update = new();
    private UpdateInfo? _pendingUpdate;
    private CancellationTokenSource? _cts;
    private string? _lastOutputDir;

    public MainWindow()
    {
        InitializeComponent();
        FileList.ItemsSource = _rows;

        var s = App.Settings;
        SwColor.IsChecked = s.EnableAutoColor;
        SwDecensor.IsChecked = s.EnableDecensor;
        SwTranslate.IsChecked = s.EnableTranslate;

        TxtVersion.Text = $"v{_update.CurrentVersion}";
        RefreshComfyBadges();
        Loaded += async (_, _) =>
        {
            if (((App)System.Windows.Application.Current).PreloadDir is { } dir && Directory.Exists(dir))
                AddPaths([dir]);   // --demo-files: UI 확인용 미리 채우기
            await CheckUpdateAsync();
        };
    }

    /// <summary>ComfyUI가 필요한 기능(채색·강력 검열) 카드에 설치 필요 배지를 띄운다.</summary>
    private void RefreshComfyBadges()
    {
        var comfyOk = App.Settings.ComfyAvailable;
        BadgeColor.Visibility = comfyOk ? Visibility.Collapsed : Visibility.Visible;
        var strong = App.Settings.DecensorMode == "Strong";
        BadgeDecensor.Visibility = !comfyOk && strong ? Visibility.Visible : Visibility.Collapsed;
        HintDecensor.Text = strong
            ? "모자이크를 SDXL로 재생성 (강력 모드)"
            : "모자이크를 찾아 자연스럽게 복원 (기본·내장 AI)";
    }

    // ── 파일 추가 ──────────────────────────────────────────────
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
    }

    private void BtnPick_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "만화 이미지|" + string.Join(";", ImageExts.Select(e => "*" + e)) + "|모든 파일|*.*",
        };
        if (dlg.ShowDialog() == true) AddPaths(dlg.FileNames);
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)
                         .Where(f => ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    AddFile(f);
            }
            else if (File.Exists(p) && ImageExts.Contains(Path.GetExtension(p).ToLowerInvariant()))
                AddFile(p);
        }
        UpdateDropHint();
    }

    private void AddFile(string path)
    {
        if (_rows.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        var row = new FileRow { Path = path };
        _rows.Add(row);
        _ = LoadThumbAsync(row);
    }

    private void UpdateDropHint()
    {
        var has = _rows.Count > 0;
        DropHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        ListPane.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        TxtStatus.Text = has ? $"{_rows.Count}장 준비됨 ♪" : "준비 완료 ♪";
        if (has && FileList.SelectedIndex < 0) FileList.SelectedIndex = 0;
    }

    /// <summary>목록 썸네일을 백그라운드에서 만든다. DecodePixelHeight로 축소 디코딩해
    /// 원고 수십 장을 올려도 메모리·시간이 튀지 않는다.</summary>
    private static async Task LoadThumbAsync(FileRow row)
    {
        var path = row.ViewPath;
        var thumb = await Task.Run(() => LoadBitmap(path, 160));
        row.Thumb = thumb;
    }

    private static BitmapSource? LoadBitmap(string path, int decodeHeight)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;      // 파일 잠금을 남기지 않는다
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path);
            if (decodeHeight > 0) bmp.DecodePixelHeight = decodeHeight;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }   // webp 등 WPF가 못 읽는 포맷은 썸네일 없이 간다
    }

    /// <summary>작업이 끝난 뒤 결과물 쪽을 보도록 경로를 바꾸고 썸네일·미리보기를 갱신한다.</summary>
    private void RefreshResultViews(PipelineRunner runner)
    {
        foreach (var row in _rows)
        {
            var outPath = runner.OutputPathFor(row.Path);
            if (!File.Exists(outPath))
            {
                // 검열이 없던 장은 원본이 확장자 그대로 복사된다
                var sameName = Path.Combine(Path.GetDirectoryName(outPath)!, Path.GetFileName(row.Path));
                outPath = File.Exists(sameName) ? sameName : row.Path;
            }
            row.ViewPath = outPath;
            _ = LoadThumbAsync(row);
        }
        ShowPreview(FileList.SelectedItem as FileRow ?? _rows.FirstOrDefault());
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ShowPreview(FileList.SelectedItem as FileRow);

    private void ShowPreview(FileRow? row)
    {
        if (row == null) { ImgPreview.Source = null; TxtPreviewName.Text = "미리보기"; return; }
        TxtPreviewName.Text = Path.GetFileName(row.ViewPath);
        ImgPreview.Source = LoadBitmap(row.ViewPath, 1400);
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        _rows.Clear();
        UpdateDropHint();
    }

    // ── 실행 ──────────────────────────────────────────────────
    private void Feature_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var s = App.Settings;
        s.EnableAutoColor = SwColor.IsChecked == true;
        s.EnableDecensor = SwDecensor.IsChecked == true;
        s.EnableTranslate = SwTranslate.IsChecked == true;
        s.Save();
    }

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            TxtStatus.Text = "먼저 이미지를 올려주세요 (・ω・`)";
            return;
        }
        var s = App.Settings;
        if (!s.EnableAutoColor && !s.EnableDecensor && !s.EnableTranslate)
        {
            TxtStatus.Text = "켜진 기능이 없어요 — 위 토글을 하나 이상 켜주세요";
            return;
        }

        // ComfyUI가 필요한 조합인데 미설치면 바로 안내
        bool needComfy = s.EnableAutoColor || (s.EnableDecensor && s.DecensorMode == "Strong");
        if (needComfy && !s.ComfyAvailable)
        {
            TxtStatus.Text = "채색/강력 검열은 ComfyUI가 필요해요 — 설정 → 구성요소에서 설치해 주세요";
            return;
        }

        _cts = new CancellationTokenSource();
        BtnRun.IsEnabled = false;
        BtnStop.IsEnabled = true;
        TxtStatus.Text = "작업 중... (ฅ'ω'ฅ)";

        using var runner = new PipelineRunner(s);
        var files = _rows.Select(r => r.Path).ToList();
        _lastOutputDir = Path.GetDirectoryName(runner.OutputPathFor(files[0]));

        var progress = new Progress<PageProgress>(p =>
        {
            var row = _rows.FirstOrDefault(r => r.Name == p.File);
            if (row == null) return;
            row.Stage = p.Error != null ? $"실패 — {p.Error}" : p.Stage;
            row.Percent = p.Percent;
        });

        try
        {
            await Task.Run(() => runner.RunAsync(files, progress, _cts.Token));
            RefreshResultViews(runner);   // 목록 썸네일·미리보기를 결과물로 바꿔 바로 확인할 수 있게
            var failed = _rows.Count(r => r.Stage.StartsWith("실패"));
            TxtStatus.Text = failed == 0
                ? $"전부 완료! 총 {_rows.Count}장 (ﾉ´ヮ`)ﾉ*:･ﾟ✧"
                : $"완료 — 성공 {_rows.Count - failed}장 / 실패 {failed}장";
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "멈췄어요";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "오류: " + ex.Message;
        }
        finally
        {
            _cts = null;
            BtnRun.IsEnabled = true;
            BtnStop.IsEnabled = false;
        }
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void BtnOpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var dir = !string.IsNullOrWhiteSpace(App.Settings.OutputRoot) ? App.Settings.OutputRoot : _lastOutputDir;
        if (dir != null && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        else
            TxtStatus.Text = "아직 결과물이 없어요";
    }

    /// <summary>목록에서 고른 원고의 결과물을 띄운다. 자동 감지가 놓친 검열을 거기서 직접 지울 수 있다.
    /// 결과 파일이 없으면(아직 안 돌렸거나 검열이 없어 원본이 그대로 나간 경우) 원본을 연다.</summary>
    private void OpenResultFor(FileRow? row)
    {
        row ??= _rows.FirstOrDefault();
        if (row == null)
        {
            TxtStatus.Text = "먼저 이미지를 올려주세요";
            return;
        }

        // 창 안에서 ◀ ▶로 넘겨볼 수 있게 목록 전체를 넘긴다
        var pages = _rows.Select(r => r.ViewPath).ToList();
        var win = new ResultWindow(pages, Math.Max(0, _rows.IndexOf(row))) { Owner = this };
        win.ShowDialog();

        // 창에서 고친 내용이 목록에도 바로 보이게
        foreach (var r in _rows) _ = LoadThumbAsync(r);
        ShowPreview(FileList.SelectedItem as FileRow ?? row);
    }

    private void BtnViewResult_Click(object sender, RoutedEventArgs e)
        => OpenResultFor(FileList.SelectedItem as FileRow);

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => OpenResultFor(FileList.SelectedItem as FileRow);

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow { Owner = this }.ShowDialog();
        // 설정 창에서 토글·검열 모드·구성요소가 바뀌었을 수 있다
        var s = App.Settings;
        SwColor.IsChecked = s.EnableAutoColor;
        SwDecensor.IsChecked = s.EnableDecensor;
        SwTranslate.IsChecked = s.EnableTranslate;
        RefreshComfyBadges();
    }

    // ── 업데이트 ──────────────────────────────────────────────
    private async Task CheckUpdateAsync()
    {
        try
        {
            _pendingUpdate = await _update.CheckAsync();
            if (_pendingUpdate != null)
            {
                BtnUpdate.Visibility = Visibility.Visible;
                BtnUpdate.Content = $"🎀 v{_pendingUpdate.TargetFullRelease.Version} 업데이트!";
            }
        }
        catch { /* 오프라인 등 — 조용히 넘어간다 */ }
    }

    private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate == null) return;
        BtnUpdate.IsEnabled = false;
        try
        {
            await _update.DownloadAndApplyAsync(_pendingUpdate,
                p => Dispatcher.Invoke(() => TxtStatus.Text = $"업데이트 다운로드 {p}%"));
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "업데이트 실패: " + ex.Message;
            BtnUpdate.IsEnabled = true;
        }
    }
}
