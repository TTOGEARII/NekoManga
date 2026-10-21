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

    /// <summary>ComfyUI가 필요한 기능(채색·엔진 LaMa 검열) 카드에 설치 필요 배지를 띄운다.</summary>
    private void RefreshComfyBadges()
    {
        var comfyOk = App.Settings.ComfyAvailable;
        BadgeColor.Visibility = comfyOk ? Visibility.Collapsed : Visibility.Visible;
        var comfyLama = App.Settings.DecensorMode is "ComfyLama" or "ComfyRegen" or "ComfyCustom";
        BadgeDecensor.Visibility = !comfyOk && comfyLama ? Visibility.Visible : Visibility.Collapsed;
        // 검열 감지 AI(전용 학습 모델) 유무·복원 방식에 맞춰 설명을 바꾼다
        bool censorAi = App.Settings.DecensorTarget == "Auto" &&
            File.Exists(Path.Combine(App.Settings.RuntimeRoot, "models", NekoManga.Core.Engine.CensorNet.ModelFile));
        string find = censorAi ? "AI가 검열을 찾아" : "검열을 찾아";
        HintDecensor.Text = App.Settings.DecensorMode switch
        {
            "ComfyLama" => $"{find} 엔진 LaMa로 복원 (GPU)",
            "ComfyRegen" => $"{find} SDXL로 재생성 (GPU·내장 레시피)",
            "ComfyCustom" => $"{find} 커스텀 ComfyUI 워크플로로 복원 (GPU)",
            _ => $"{find} 자연스럽게 복원 (기본·내장 AI)",
        };
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
                         .Where(f => ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant())))
                    AddFile(f);
            }
            else if (File.Exists(p) && ImageExts.Contains(Path.GetExtension(p).ToLowerInvariant()))
                AddFile(p);
        }
        SortRowsByName();   // 불러온 순서(드롭·탐색기 선택 순서)와 무관하게 늘 이름 오름차순 (사용자 요청 2026-10-21)
        UpdateDropHint();
    }

    /// <summary>탐색기와 같은 자연 정렬(2 < 10)로 목록을 이름 오름차순 정렬. 같은 Row 객체를 재사용해 썸네일·상태를 유지한다.</summary>
    private bool _sortingRows;
    private void SortRowsByName()
    {
        if (_cts != null || _rows.Count < 2) return;   // 작업 중엔 목록을 흔들지 않는다 (진행 표시는 이름으로 찾으므로 정렬 자체는 안전)
        var sorted = _rows.OrderBy(r => r.Name, Comparer<string>.Create(StrCmpLogicalW)).ToList();
        if (sorted.SequenceEqual(_rows)) return;
        var selected = FileList.SelectedItems.Cast<FileRow>().ToList();
        _sortingRows = true;   // Clear/Add가 일으키는 선택 변경으로 미리보기가 깜빡이며 다시 디코딩되지 않게
        try
        {
            _rows.Clear();
            foreach (var r in sorted) _rows.Add(r);
            foreach (var r in selected) FileList.SelectedItems.Add(r);
        }
        finally { _sortingRows = false; }
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
    /// 원고 수십 장을 올려도 메모리·시간이 튀지 않는다.
    /// 동시 디코딩은 3장으로 제한 — 결과물 PNG(채색본 수 MB)를 수십 장 한꺼번에 풀면 스레드풀이 꽉 차
    /// 미리보기 디코딩까지 뒤로 밀려 "완료 후 그림이 늦게 뜨는" 체감이 됐다 (2026-10-21).</summary>
    private static readonly SemaphoreSlim ThumbGate = new(3);
    private static async Task LoadThumbAsync(FileRow row)
    {
        var path = row.ViewPath;
        await ThumbGate.WaitAsync();
        try
        {
            if (row.ViewPath != path) return;   // 기다리는 사이 경로가 바뀌었으면(결과물로 교체) 그쪽 요청이 다시 온다
            var thumb = await Task.Run(() => LoadBitmap(path, 160));
            if (row.ViewPath == path) row.Thumb = thumb;
        }
        finally { ThumbGate.Release(); }
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

    /// <summary>이 장의 결과물 경로 (없으면 원본). 검열이 없던 장은 원본이 확장자 그대로 복사된다.</summary>
    private static string ResolveViewPath(PipelineRunner runner, FileRow row)
    {
        var outPath = runner.OutputPathFor(row.Path);
        if (File.Exists(outPath)) return outPath;
        var sameName = Path.Combine(Path.GetDirectoryName(outPath)!, Path.GetFileName(row.Path));
        return File.Exists(sameName) ? sameName : row.Path;
    }

    /// <summary>한 장이 끝날 때마다 그 장만 결과물로 바꿔 보여 준다 — 전체가 끝날 때까지 기다리지 않는다 (2026-10-21).</summary>
    private void ShowResultFor(PipelineRunner runner, FileRow row)
    {
        var path = ResolveViewPath(runner, row);
        if (path == row.ViewPath) return;
        row.ViewPath = path;
        _ = LoadThumbAsync(row);
        if (ReferenceEquals(FileList.SelectedItem, row)) ShowPreview(row);
    }

    /// <summary>작업이 끝난 뒤 결과물 쪽을 보도록 경로를 바꾸고 썸네일·미리보기를 갱신한다 (진행 중 놓친 장 보정용).</summary>
    private void RefreshResultViews(PipelineRunner runner)
    {
        foreach (var row in _rows)
        {
            var path = ResolveViewPath(runner, row);
            if (path == row.ViewPath) continue;
            row.ViewPath = path;
            _ = LoadThumbAsync(row);
        }
        ShowPreview(FileList.SelectedItem as FileRow ?? _rows.FirstOrDefault());
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sortingRows) return;
        ShowPreview(FileList.SelectedItem as FileRow);
    }

    /// <summary>큰 미리보기. 디코딩은 백그라운드에서 — 결과물 PNG(2000×3000 채색본)를 UI 스레드에서 풀면 0.5~1.5초 멈춘다.
    /// 디코딩 높이는 패널 크기에 맞춘다(기본 380px 폭 패널에 1400px을 풀던 것은 낭비). 늦게 끝난 옛 요청은 세대 번호로 버린다.</summary>
    private int _previewGen;
    private async void ShowPreview(FileRow? row)
    {
        int gen = ++_previewGen;
        if (row == null) { ImgPreview.Source = null; TxtPreviewName.Text = "미리보기"; return; }
        var path = row.ViewPath;
        TxtPreviewName.Text = Path.GetFileName(path);
        // 패널 실제 높이 × DPI × 1.5 (확대 여유), 600~1600 사이
        double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleY;
        int h = (int)Math.Clamp((ImgPreview.ActualHeight > 50 ? ImgPreview.ActualHeight : 700) * dpi * 1.5, 600, 1600);
        var bmp = await Task.Run(() => LoadBitmap(path, h));
        if (gen != _previewGen) return;            // 그 사이 다른 장을 골랐거나 경로가 바뀜
        ImgPreview.Source = bmp;
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        _rows.Clear();
        UpdateDropHint();
    }

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    /// <summary>탐색기와 같은 자연 정렬 — "2.png"가 "10.png"보다 앞에 온다.</summary>
    private void BtnSort_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null || _rows.Count < 2) return;
        SortRowsByName();
        TxtStatus.Text = "이름순으로 정렬했어요";
    }

    /// <summary>선택한 항목을 목록에서 뺀다 (디스크의 파일은 건드리지 않는다). Delete 키와 공용.</summary>
    private void RemoveSelected()
    {
        if (_cts != null || FileList.SelectedItems.Count == 0) return;
        int idx = FileList.SelectedIndex;
        foreach (var row in FileList.SelectedItems.Cast<FileRow>().ToList())
            _rows.Remove(row);
        if (_rows.Count > 0) FileList.SelectedIndex = Math.Min(idx, _rows.Count - 1);
        UpdateDropHint();
    }

    private void BtnRemoveSel_Click(object sender, RoutedEventArgs e) => RemoveSelected();

    private void FileList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Delete) { RemoveSelected(); e.Handled = true; }
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
        bool needComfy = s.EnableAutoColor
            || (s.EnableDecensor && s.DecensorMode is "ComfyLama" or "ComfyRegen" or "ComfyCustom");
        if (needComfy && !s.ComfyAvailable)
        {
            TxtStatus.Text = "채색/엔진 LaMa·재생성 검열은 ComfyUI가 필요해요 — 설정 → 구성요소에서 설치해 주세요";
            return;
        }

        _cts = new CancellationTokenSource();
        BtnRun.IsEnabled = false;
        BtnStop.IsEnabled = true;
        TxtStatus.Text = "작업 중... (ฅ'ω'ฅ)";

        using var runner = new PipelineRunner(s);
        // 멈췄다 재시작하면 이미 끝난 장은 건너뛴다 (상태가 "완료"인 행 — 이번 세션에서 끝난 것)
        var pending = _rows.Where(r => r.Stage != "완료").ToList();
        int skipped = _rows.Count - pending.Count;
        if (pending.Count == 0)
        {
            TxtStatus.Text = "모든 장이 이미 완료됐어요 — 다시 돌리려면 목록을 비우고 새로 올려주세요";
            _cts = null; BtnRun.IsEnabled = true; BtnStop.IsEnabled = false;
            return;
        }
        if (skipped > 0) TxtStatus.Text = $"완료된 {skipped}장은 건너뛰고 {pending.Count}장 작업 중...";
        var files = pending.Select(r => r.Path).ToList();
        _lastOutputDir = Path.GetDirectoryName(runner.OutputPathFor(files[0]));

        var progress = new Progress<PageProgress>(p =>
        {
            var row = _rows.FirstOrDefault(r => r.Name == p.File);
            if (row == null) return;
            row.Stage = p.Error != null ? $"실패 — {p.Error}" : p.Stage;
            row.Percent = p.Percent;
            if (p.Done && p.Error == null) ShowResultFor(runner, row);   // 끝난 장은 바로 결과물로
        });

        // 검토 흐름(설정 DecensorReview): 러너가 1차(번역+감지)를 끝내면 UI 스레드에서 검토 창을 띄워 마스크를 고치게 한다
        runner.ReviewMasks = (pages, token) => Dispatcher.InvokeAsync(() =>
        {
            TxtStatus.Text = "검열 마스크 검토 중 — 창에서 확인·수정 후 [검열 제거 시작]을 누르세요";
            var w = new MaskReviewWindow(pages) { Owner = this };
            return w.ShowDialog() == true;
        }).Task;

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
