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

    /// <summary>이번 세션에서 끝난 장(러너가 Done·오류 없음으로 보고) — 멈췄다 재시작할 때 건너뛴다.
    /// 상태 문자열로 가리면 "검열 없음 — 원본 그대로"·경고 달린 완료가 빠져서 플래그로 둔다.</summary>
    public bool Done { get; set; }
    /// <summary>끝난 장의 결과물 경로(사이드카 위치) — 작업 뒤 설정의 출력 폴더를 바꿔도 이 장의 결과·사이드카는 여기 있다.</summary>
    public string? OutPath { get; set; }

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
    /// <summary>OpenCV가 읽을 수 있는 포맷 — Core와 같은 목록.</summary>
    private static string[] ImageExts => NekoManga.Core.ImgIo.ImageExts;

    private readonly ObservableCollection<FileRow> _rows = [];
    private readonly UpdateService _update = new();
    private UpdateInfo? _pendingUpdate;
    private CancellationTokenSource? _cts;
    private string? _lastOutputDir;
    /// <summary>실행 중인 러너 — 페이지별 검토의 '진행' 확인(ConfirmPage/ConfirmAll)을 결과 창·툴바가 보낼 수 있게 (2026-10-21).</summary>
    private PipelineRunner? _runner;

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
                         .Where(NekoManga.Core.ImgIo.IsImageFile))
                    AddFile(f);
            }
            else if (File.Exists(p) && NekoManga.Core.ImgIo.IsImageFile(p))
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
    private string ResolveViewPath(FileRow row) => PipelineRunner.ResolveResultPath(OutPathOf(row), row.Path);

    /// <summary>행의 결과물 경로 — 끝난 장은 끝날 때 기억한 경로(그 뒤 출력 폴더 설정이 바뀌어도), 아니면 지금 설정으로 계산 (이름은 원고 하나로 정해진다).</summary>
    private string OutPathOf(FileRow row) =>
        row.Done && row.OutPath != null ? row.OutPath : PipelineRunner.OutputPathFor(_runPathSettings ?? App.Settings, row.Path);

    /// <summary>작업 중 경로 계산에 쓰는 설정 — 러너가 쓰는 출력 폴더(시작 때 값)로 고정. 설정 창에서 출력 폴더를 바꿔도 이번 작업의 결과물은 옛 폴더에 쓰이므로.</summary>
    private NekoManga.Core.AppSettings? _runPathSettings;

    /// <summary>한 장이 끝날 때마다 그 장만 결과물로 바꿔 보여 준다 — 전체가 끝날 때까지 기다리지 않는다 (2026-10-21).</summary>
    private void ShowResultFor(FileRow row)
    {
        // 경로가 같아도 다시 읽는다 — 결과 창이 먼저 저장해 둔 자리에 2차가 최종본을 덮어쓴 경우 중간본이 남지 않게
        var path = ResolveViewPath(row);
        row.ViewPath = path;
        _ = LoadThumbAsync(row);
        if (ReferenceEquals(FileList.SelectedItem, row)) ShowPreview(row);
    }

    /// <summary>작업이 끝난 뒤 결과물 쪽을 보도록 경로를 바꾸고 썸네일·미리보기를 갱신한다 (진행 중 놓친 장 보정용).</summary>
    private void RefreshResultViews()
    {
        foreach (var row in _rows.Where(r => r.Done))   // 끝난 장만 — 멈춤·실패·대기 행까지 풀면 지난 세션 결과물이 이번 결과인 양 보인다
        {
            var path = ResolveViewPath(row);
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
        var s = App.Settings;   // 러너는 살아 있는 설정을 쓴다 — 표지 색 태그 캐시(ColorRefTags)를 거기 써서 저장하므로 사본이면 캐시가 안 남는다
        var pathSnapshot = App.Settings.ShallowClone();   // 경로 계산만 시작 때 출력 폴더로 고정 — 작업 중 설정 창에서 바꿔도 이 작업의 결과물은 옛 폴더에 쓰인다 (러너도 같은 값을 박제한다)
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

        // 멈췄다 재시작하면 이미 끝난 장은 건너뛴다 (Done 행 — 이번 세션에서 끝난 것; "검열 없음 — 원본 그대로"도 끝난 장이다)
        var pending = _rows.Where(r => !r.Done).ToList();
        int skipped = _rows.Count - pending.Count;
        if (pending.Count == 0)
        {
            // 전부 끝난 목록에서 다시 [작업 시작] = 전부 다시 돌리라는 뜻(설정을 바꿔 다시 돌리는 흔한 흐름) — 결과물을 덮어쓰니 한 번 묻는다
            if (System.Windows.MessageBox.Show($"모든 장({_rows.Count}장)이 이미 완료됐어요. 전부 다시 돌릴까요?\n결과물과 저장된 마스크가 새 결과로 바뀝니다.", "다시 돌리기",
                    System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
            {
                TxtStatus.Text = "그대로 두었어요";
                return;
            }
            foreach (var r in _rows)
            {
                r.Done = false; r.OutPath = null; r.Stage = "대기"; r.Percent = 0;
                if (r.ViewPath != r.Path) { r.ViewPath = r.Path; _ = LoadThumbAsync(r); }   // 이번에 실패·멈춘 장이 지난 결과를 이번 것인 양 보이지 않게
            }
            pending = _rows.ToList(); skipped = 0;
        }
        _cts = new CancellationTokenSource();
        BtnRun.IsEnabled = false;
        BtnStop.IsEnabled = true;
        TxtStatus.Text = skipped > 0 ? $"완료된 {skipped}장은 건너뛰고 {pending.Count}장 작업 중..." : "작업 중... (ฅ'ω'ฅ)";

        using var runner = new PipelineRunner(s);
        _runner = runner;
        var files = pending.Select(r => r.Path).ToList();
        _runPathSettings = pathSnapshot;
        _lastOutputDir = PipelineRunner.OutputDirFor(pathSnapshot, files[0]);

        var progress = new Progress<PageProgress>(p =>
        {
            // 전체 경로로 찾는다 — 이름만 보면 vol1\01.jpg·vol2\01.jpg가 한 행에 겹친다
            var row = _rows.FirstOrDefault(r => string.Equals(r.Path, p.File, StringComparison.OrdinalIgnoreCase));
            if (row == null) return;
            row.Stage = p.Error != null ? $"실패 — {p.Error}"
                      : p.Stage.StartsWith("검토 대기 · ") ? "검토 대기 (더블클릭해 수정) · " + p.Stage["검토 대기 · ".Length..]
                      : p.Stage;
            if (p.Percent >= 0) row.Percent = p.Percent;   // 경고 보고(-1)는 진행률을 건드리지 않는다
            if (p.Done && p.Error == null) { row.Done = true; row.OutPath = PipelineRunner.OutputPathFor(pathSnapshot, row.Path); ShowResultFor(row); }   // 끝난 장은 바로 결과물로 (경로는 이 작업의 폴더로 박제)
            RefreshWaitingUi();
        });

        // 페이지별 검토(DecensorReviewMode=PerPage): 감지된 장은 '검토 대기'로 남는다 — 더블클릭 → 결과 창 마스크 탭에서 고친 뒤 [검열 제거 진행]
        runner.PageWaiting += pg => Dispatcher.BeginInvoke(() =>
        {
            RefreshWaitingUi();   // 행 상태 글은 진행 보고(progress)가 쓴다 — 두 경로가 순서 없이 겹치지 않게
            TxtStatus.Text = "검열이 감지된 장은 '검토 대기'예요 — 더블클릭해 마스크를 확인·수정하고 진행하세요 (나머지 장은 계속 처리 중)";
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
            RefreshResultViews();         // 목록 썸네일·미리보기를 결과물로 바꿔 바로 확인할 수 있게
            var failed = _rows.Count(r => r.Stage.StartsWith("실패"));
            TxtStatus.Text = failed == 0
                ? $"전부 완료! 총 {_rows.Count}장 (ﾉ´ヮ`)ﾉ*:･ﾟ✧"
                : $"완료 — 성공 {_rows.Count - failed}장 / 실패 {failed}장";
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "멈췄어요";
            // 진행 중이던 장이 '번역 중' 같은 상태로 남지 않게 — 끝난(완료/실패) 장만 그대로 둔다
            foreach (var r in _rows)
                if (!r.Done && !r.Stage.StartsWith("실패") && r.Stage != "대기")
                    r.Stage = "멈춤";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "오류: " + ex.Message;
        }
        finally
        {
            _cts = null;
            _runner = null;
            _runPathSettings = null;
            BtnRun.IsEnabled = true;
            BtnStop.IsEnabled = false;
            RefreshWaitingUi();
        }
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    /// <summary>검토 대기 중인 장이 있을 때만 [대기 중 전부 진행] 버튼을 보인다.</summary>
    private void RefreshWaitingUi()
    {
        int n = _runner?.WaitingFiles.Count ?? 0;
        BtnProceedAll.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (n > 0) BtnProceedAll.Content = $"대기 중 {n}장 전부 진행";
    }

    private void BtnProceedAll_Click(object sender, RoutedEventArgs e)
    {
        int n = _runner?.ConfirmAll() ?? 0;
        TxtStatus.Text = n > 0 ? $"검토 대기 {n}장을 지금 마스크 그대로 검열 제거로 보냈어요" : "검토 대기 중인 장이 없어요";
        RefreshWaitingUi();
    }

    private bool IsWaiting(string file) => _runner?.WaitingFiles.Contains(file, StringComparer.OrdinalIgnoreCase) == true;

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

        // 창 안에서 ◀ ▶로 넘겨볼 수 있게 목록 전체를 넘긴다 (결과물 경로도 함께 — .neko 사이드카 위치)
        var pages = _rows.Select(r => new ResultWindow.PageRef(r.ViewPath, OutPathOf(r), r.Path)).ToList();
        var win = new ResultWindow(pages, Math.Max(0, _rows.IndexOf(row)),
            confirmPage: f => { var ok = _runner?.ConfirmPage(f) == true; if (ok) RefreshWaitingUi(); return ok; },
            isWaiting: IsWaiting,
            startOnMask: IsWaiting(row.Path),
            isDone: f => _rows.Any(r => r.Done && string.Equals(r.Path, f, StringComparison.OrdinalIgnoreCase))) { Owner = this };
        win.ShowDialog();

        // 창에서 고친 내용이 목록에도 바로 보이게 — 창이 저장하거나 다시 돌린 행만 결과물로 다시 푼다
        // (전 행을 풀면 아직 안 돌린 '대기' 행이 지난 세션 결과를 보여 주고, 썸네일을 전부 다시 풀면 바뀐 장의 미리보기가 뒤로 밀린다)
        foreach (var r in _rows)
        {
            if (!win.ChangedSources.Contains(r.Path)) continue;
            var path = ResolveViewPath(r);
            if (path != r.ViewPath) r.ViewPath = path;
            _ = LoadThumbAsync(r);
        }
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
