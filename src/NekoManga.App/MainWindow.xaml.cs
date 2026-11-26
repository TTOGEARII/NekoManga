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
using NekoManga.Core;

namespace NekoManga.App;

public class FileRow : INotifyPropertyChanged
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>결과 창에서 마스크를 고쳐 저장했고 아직 결과물에 반영하지 않은 장(MaskSidecar.Pending) — 목록의 이름 앞에 ✎로 보인다(결과 창 페이지 콤보와 같은 표시).</summary>
    private bool _edited;
    public bool Edited { get => _edited; set { if (_edited == value) return; _edited = value; Notify(nameof(Edited)); Notify(nameof(Label)); } }
    public string Label => (Edited ? "✎ " : "") + Name;

    private string _stage = Loc.T("대기");
    public string Stage { get => _stage; set { _stage = value; Notify(nameof(Stage)); } }
    /// <summary>진행 보고를 한 번이라도 받은 행 — 멈췄을 때 '멈춤'으로 바꿀 행을 가린다. 전에는 상태 글이 "대기"인지로 가렸다:
    /// 상태 글은 화면 언어를 따라 바뀌므로(Loc) 견주는 데 쓰지 않는다. 전부 다시 돌릴 때 false로 되돌린다.</summary>
    public bool Started { get; set; }
    /// <summary>이번 작업에서 실패한 행(러너가 오류를 보고) — 끝 집계가 세고 '멈춤' 표시가 건너뛴다. 전에는 상태 글이 "실패"로 시작하는지를 봤다.</summary>
    public bool Failed { get; set; }

    private double _percent;
    public double Percent { get => _percent; set { _percent = value; Notify(nameof(Percent)); } }

    /// <summary>이번 세션에서 끝난 장(러너가 Done·오류 없음으로 보고) — 멈췄다 재시작할 때 건너뛴다.
    /// 상태 문자열로 가리면 "검열 없음 — 원본 그대로다냥"·경고 달린 완료가 빠져서 플래그로 둔다.</summary>
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
        UiText.Apply(this);   // XAML에 적힌 글을 지금 화면 언어로 — 뜨기 전에
        Loc.Changed += OnLanguageChanged;
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

    private bool _syncingSwitches;

    /// <summary>기능 스위치와 배지를 지금 설정으로 다시 채운다 — 이 창을 만든 뒤 다른 창이 설정을 바꿨을 때.
    /// 첫 실행에서는 이 창이 준비 창보다 먼저 만들어진다: 준비 창의 PC 맞춤 설정이 자동 채색을 꺼도 스위치는 켜진 채 보였고,
    /// 다른 스위치를 건드리면 Feature_Changed가 세 값을 모두 스위치에서 읽어 자동 채색을 도로 켜서 저장했다.
    /// 채우는 동안에는 Feature_Changed가 저장하지 않게 막는다 — 스위치를 하나씩 바꾸는 사이 나머지 스위치의 옛 화면 값이 설정을 덮지 않게.</summary>
    internal void SyncFromSettings()
    {
        var s = App.Settings;
        _syncingSwitches = true;
        try
        {
            SwColor.IsChecked = s.EnableAutoColor;
            SwDecensor.IsChecked = s.EnableDecensor;
            SwTranslate.IsChecked = s.EnableTranslate;
        }
        finally { _syncingSwitches = false; }
        RefreshComfyBadges();
    }

    /// <summary>ComfyUI가 필요한 기능(채색·재생성·커스텀 검열) 카드에 설치 필요 배지를 띄운다.</summary>
    private void RefreshComfyBadges()
    {
        var comfyOk = App.Settings.ComfyAvailable;
        BadgeColor.Visibility = comfyOk ? Visibility.Collapsed : Visibility.Visible;
        var comfyDecensor = App.Settings.DecensorMode is "ComfyRegen" or "ComfyCustom";
        BadgeDecensor.Visibility = !comfyOk && comfyDecensor ? Visibility.Visible : Visibility.Collapsed;
        // 전용 AI 유무·복원 방식에 맞춰 설명을 바꾼다 — 문구는 설정 창 요약과 같은 곳(DecensorLabels)에서
        bool censorNet = File.Exists(Path.Combine(App.Settings.RuntimeRoot, "models", NekoManga.Core.Engine.CensorNet.ModelFile));
        HintDecensor.Text = DecensorLabels.Card(App.Settings);
        var find = DecensorLabels.Find(App.Settings.DecensorTarget, App.Settings.DecensorLegacyAssist, censorNet);
        var fix = DecensorLabels.Fix(App.Settings.DecensorMode, App.Settings.DecensorTarget, App.Settings.DecensorLegacyAssist, App.Settings.DecensorAssistRepair);
        HintDecensor.ToolTip = Loc.F($"① 찾기: {find}\n② 지우기: {fix}\n(⚙ 설정 → 검열 제거에서 바꿀 수 있다냥)");
    }

    // ── 파일 추가 ──────────────────────────────────────────────
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _cts == null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>작업이 도는 동안에는 목록에 원고를 더 받지 않는다(행 지우기·정렬과 같다) — 받으면 그 행은 이번 작업에 들지도 않으면서
    /// 상태 줄이 "N장 준비됨"으로 덮이고 끝 집계("총 N장")에 섞였다(2026-11-13 점검 10번).</summary>
    private bool RefuseWhileRunning()
    {
        if (_cts == null) return false;
        TxtStatus.Text = Loc.T("작업이 도는 중이다냥 — 원고는 끝난 뒤에 올려 줘냥");
        return true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
    }

    private void BtnPick_Click(object sender, RoutedEventArgs e)
    {
        if (RefuseWhileRunning()) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = Loc.T("만화 이미지") + "|" + string.Join(";", ImageExts.Select(e => "*" + e)) + "|" + Loc.T("모든 파일") + "|*.*",
        };
        if (dlg.ShowDialog() == true) AddPaths(dlg.FileNames);
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        if (RefuseWhileRunning()) return;
        var skipped = new List<string>();   // 받지 않은 파일 — 읽지 못하는 그림(.avif 등)이 섞였으면 아래에서 알린다
        var skippedDirs = new List<string>();   // 건너뛴 결과 폴더
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                // 하위 폴더까지 받되 앱이 만든 폴더(그 폴더의 결과 폴더 <이름>_neko, 사이드카 폴더 .neko, 고정한 출력 폴더)는 뺀다 — 결과 폴더는 원고 폴더 안에 생겨서,
                // 한 번 돌린 폴더를 다시 끌어다 놓으면 결과물과 마스크 그림까지 원고로 들어왔다(2026-11-13 점검 6번). 결과 폴더를 직접 끌어다 놓으면 그 안의 결과물은 받는다
                foreach (var f in PipelineRunner.EnumerateSourceFiles(p, App.Settings, skippedDirs))
                    if (NekoManga.Core.ImgIo.IsImageFile(f)) AddFile(f);
                    else skipped.Add(f);
            }
            else if (File.Exists(p))
            {
                if (NekoManga.Core.ImgIo.IsImageFile(p)) AddFile(p);
                else skipped.Add(p);
            }
        }
        SortRowsByName();   // 불러온 순서(드롭·탐색기 선택 순서)와 무관하게 늘 이름 오름차순 (사용자 요청 2026-10-21)
        UpdateDropHint();
        // 조용히 버리면 왜 목록에 안 들어왔는지 모른다(.avif는 전에 받았다가 작업 도중 실패했다) — 그림이 아닌 파일은 세지 않는다
        if (NekoManga.Core.ImgIo.UnreadableNotice(skipped) is { } notice)
            TxtStatus.Text = _rows.Count > 0 ? $"{TxtStatus.Text} — {notice}" : notice;
        if (skippedDirs.Count > 0)
        {
            var which = skippedDirs.Count == 1 ? Path.GetFileName(skippedDirs[0]) : Loc.F($"{Path.GetFileName(skippedDirs[0])} 등 {skippedDirs.Count}개");
            TxtStatus.Text += Loc.F($" — 결과 폴더({which})의 결과물은 건너뛰었다냥");
        }
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
        TxtStatus.Text = _idleStatus = IdleStatus();
        if (has && FileList.SelectedIndex < 0) FileList.SelectedIndex = 0;
        RefreshEditedUi();   // 목록이 바뀌었다 — 지난번에 마스크만 고쳐 둔 장이 들어왔을 수 있다
    }

    private string IdleStatus() => _rows.Count > 0 ? Loc.F($"{_rows.Count}장 준비됐다냥 ♪") : Loc.T("준비 완료다냥 ♪");
    /// <summary>상태 줄에 마지막으로 넣은 '준비됨' 글 — 언어가 바뀌었을 때 상태 줄이 아직 이 글이면 새 언어로 바꾼다(작업 결과·오류 같은 다른 글이면 건드리지 않는다).</summary>
    private string? _idleStatus;

    /// <summary>화면 언어가 바뀌었다 — XAML의 글과 Bind로 넣은 글은 UiText가 바꾸고, 여기서는 그 밖에 코드가 채운 글을 새 언어로 다시 채운다.
    /// 살아 있는 정보는 덮지 않는다: 도는 작업의 진행 글(다음 보고부터 새 언어로 온다)과 상태 줄의 작업 결과·오류는 그대로 둔다.</summary>
    private void OnLanguageChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(OnLanguageChanged); return; }
        foreach (var r in _rows)
            if (!r.Started && !r.Done) r.Stage = Loc.T("대기");
        RefreshComfyBadges();
        RefreshWaitingUi();
        RefreshEditedUi();
        if (TxtStatus.Text == _idleStatus) TxtStatus.Text = _idleStatus = IdleStatus();
        if (FileList.SelectedItem == null) TxtPreviewName.Text = Loc.T("미리보기");
    }

    protected override void OnClosed(EventArgs e)
    {
        Loc.Changed -= OnLanguageChanged;   // 정적 이벤트 — 떼지 않으면 닫힌 창이 남는다
        base.OnClosed(e);
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
        if (row == null) { ImgPreview.Source = null; TxtPreviewName.Text = Loc.T("미리보기"); return; }
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
        TxtStatus.Text = Loc.T("이름순으로 정렬했다냥");
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
        if (!IsLoaded || _syncingSwitches) return;
        var s = App.Settings;
        s.EnableAutoColor = SwColor.IsChecked == true;
        s.EnableDecensor = SwDecensor.IsChecked == true;
        s.EnableTranslate = SwTranslate.IsChecked == true;
        try { s.Save(); }
        catch (Exception ex) { TxtStatus.Text = Loc.F($"설정 저장 실패: {ex.Message}"); return; }   // 디스크 가득·잠김 — 스위치는 바뀐 채로 두고 알린다(다음에 저장될 때 같이 적힌다)
        NoteSettingsApplyNextJob();
    }

    /// <summary>작업은 시작할 때의 설정 사본으로 돈다 — 도는 중에 스위치·설정 창에서 바꾼 것은 다음 작업부터다. 말없이 두면 왜 안 바뀌는지 모른다.</summary>
    private void NoteSettingsApplyNextJob()
    {
        if (_cts != null) TxtStatus.Text = Loc.T("바꾼 설정은 다음 작업부터다냥 — 도는 작업은 시작 때 설정 그대로다냥");
    }

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            TxtStatus.Text = Loc.T("먼저 이미지를 올려 줘냥 (・ω・`)");
            return;
        }
        if (_updating) { TxtStatus.Text = Loc.T("업데이트를 받는 중이다냥 — 곧 앱이 다시 시작된다냥"); return; }
        // 작업은 **지금** 설정의 사본으로 돈다 — 전에는 러너가 살아 있는 설정을 장마다 읽어서, 도는 중에 스위치를 건드리면 남은 장에 바로 먹었다
        // (검열 제거를 끄면 확인까지 한 장이 검열을 남긴 채 '완료', 켜면 검토 없이 지움 — 검토·방식은 시작 때 굳었는데도. 2026-11-13 점검 7번).
        // 표지 색 태그 캐시는 러너가 콜백으로 알려 주면 살아 있는 설정에 적는다(아래 ColorRefTagsCached)
        var s = App.Settings.ShallowClone();
        var pathSnapshot = s;   // 결과 경로도 같은 사본으로 — 작업 중 설정 창에서 출력 폴더를 바꿔도 이 작업의 결과물은 시작 때 폴더에 쓰인다
        if (!s.EnableAutoColor && !s.EnableDecensor && !s.EnableTranslate)
        {
            TxtStatus.Text = Loc.T("켜진 기능이 없다냥 — 위 토글을 하나 이상 켜 줘냥");
            return;
        }

        // ComfyUI가 필요한 조합인데 미설치면 바로 안내
        bool needComfy = s.UsesComfy;   // 채색 · 재생성/커스텀 검열 제거 · 글자 자리 다시 그리기(2026-11-26)
        if (needComfy && !s.ComfyAvailable)
        {
            TxtStatus.Text = Loc.T("채색·재생성·커스텀·글자 자리 다시 그리기는 ComfyUI가 필요하다냥 — 설정 → 구성요소에서 설치해 줘냥");
            return;
        }

        // 멈췄다 재시작하면 이미 끝난 장은 건너뛴다 (Done 행 — 이번 세션에서 끝난 것; "검열 없음 — 원본 그대로다냥"도 끝난 장이다)
        var pending = _rows.Where(r => !r.Done).ToList();
        int skipped = _rows.Count - pending.Count;
        if (pending.Count == 0)
        {
            // 전부 끝난 목록에서 다시 [작업 시작] = 전부 다시 돌리라는 뜻(설정을 바꿔 다시 돌리는 흔한 흐름) — 결과물을 덮어쓰니 한 번 묻는다
            if (System.Windows.MessageBox.Show(Loc.F($"모든 장({_rows.Count}장)이 이미 완료됐다냥. 전부 다시 돌릴까냥?\n결과물과 저장된 마스크가 새 결과로 바뀐다냥."), Loc.T("다시 돌리기"),
                    System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes)
            {
                TxtStatus.Text = Loc.T("그대로 두었다냥");
                return;
            }
            foreach (var r in _rows)
            {
                r.Done = false; r.OutPath = null; r.Stage = Loc.T("대기"); r.Started = r.Failed = false; r.Percent = 0;
                if (r.ViewPath != r.Path) { r.ViewPath = r.Path; _ = LoadThumbAsync(r); }   // 이번에 실패·멈춘 장이 지난 결과를 이번 것인 양 보이지 않게
            }
            pending = _rows.ToList(); skipped = 0;
        }
        _cts = new CancellationTokenSource();
        var jobDone = _jobDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BtnRun.IsEnabled = false;
        BtnStop.IsEnabled = true;
        RefreshEditedUi();   // 작업이 도는 동안에는 [고친 장 다시 렌더링]을 숨긴다
        TxtStatus.Text = skipped > 0 ? Loc.F($"완료된 {skipped}장은 건너뛰고 {pending.Count}장 작업 중이다냥...") : Loc.T("작업 중이다냥... (ฅ'ω'ฅ)");

        using var runner = new PipelineRunner(s);
        _runner = runner;
        // 표지에서 새로 뽑은 색 태그는 살아 있는 설정에 적어 둔다(다음 작업이 다시 뽑지 않게) — 그사이 표지를 바꿨거나 이미 태그가 있으면 건드리지 않는다
        runner.ColorRefTagsCached = tags => Dispatcher.BeginInvoke(() =>
        {
            var live = App.Settings;
            if (live.ColorRefImage != s.ColorRefImage || live.ColorRefTags.Trim().Length > 0) return;
            live.ColorRefTags = tags;
            try { live.Save(); } catch { }
        });
        var files = pending.Select(r => r.Path).ToList();
        _runPathSettings = pathSnapshot;
        _lastOutputDir = PipelineRunner.OutputDirFor(pathSnapshot, files[0]);

        var progress = new Progress<PageProgress>(p =>
        {
            // 전체 경로로 찾는다 — 이름만 보면 vol1\01.jpg·vol2\01.jpg가 한 행에 겹친다
            var row = _rows.FirstOrDefault(r => string.Equals(r.Path, p.File, StringComparison.OrdinalIgnoreCase));
            if (row == null) return;
            row.Started = true; row.Failed = p.Error != null;
            row.Stage = p.Error != null ? Loc.F($"실패 — {p.Error}")
                      : p.ReviewSummary != null ? Loc.F($"검토 대기 (더블클릭해 고친다냥) · {p.ReviewSummary}")
                      : p.Stage;
            if (p.Percent >= 0) row.Percent = p.Percent;   // 경고 보고(-1)는 진행률을 건드리지 않는다
            if (p.Done && p.Error == null) { row.Done = true; row.OutPath = PipelineRunner.OutputPathFor(pathSnapshot, row.Path); ShowResultFor(row); }   // 끝난 장은 바로 결과물로 (경로는 이 작업의 폴더로 박제)
            RefreshWaitingUi();
        });

        // 페이지별 검토(DecensorReviewMode=PerPage): 감지된 장은 '검토 대기'로 남는다 — 더블클릭 → 결과 창 마스크 탭에서 고친 뒤 [검열 제거 시작](대기 중인 장 전부 진행, 창 닫힘)
        runner.PageWaiting += pg => Dispatcher.BeginInvoke(() =>
        {
            RefreshWaitingUi();   // 행 상태 글은 진행 보고(progress)가 쓴다 — 두 경로가 순서 없이 겹치지 않게
            TxtStatus.Text = Loc.T("검열이 감지된 장은 '검토 대기'다냥 — 더블클릭해 마스크를 고치고 진행하라냥");
        });

        // 검토 흐름(설정 DecensorReview): 러너가 1차(번역+감지)를 끝내면 UI 스레드에서 검토 창을 띄워 마스크를 고치게 한다
        runner.ReviewMasks = (pages, token) => Dispatcher.InvokeAsync(() =>
        {
            TxtStatus.Text = Loc.T("검열 마스크 검토 중이다냥 — 고친 뒤 [검열 제거 시작]을 누르라냥");
            var w = new MaskReviewWindow(pages) { Owner = this };
            return w.ShowDialog() == true;
        }).Task;

        try
        {
            await Task.Run(() => runner.RunAsync(files, progress, _cts.Token));
            RefreshResultViews();         // 목록 썸네일·미리보기를 결과물로 바꿔 바로 확인할 수 있게
            var failed = _rows.Count(r => r.Failed);
            TxtStatus.Text = failed == 0
                ? Loc.F($"전부 완료다냥! 총 {_rows.Count}장 (ﾉ´ヮ`)ﾉ*:･ﾟ✧")
                : Loc.F($"완료 — 성공 {_rows.Count - failed}장 / 실패 {failed}장");
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = Loc.T("멈췄다냥");
            // 진행 중이던 장이 '번역 중' 같은 상태로 남지 않게 — 끝난(완료/실패) 장만 그대로 둔다
            foreach (var r in _rows)
                if (!r.Done && !r.Failed && r.Started)
                    r.Stage = Loc.T("멈춤");
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Loc.F($"오류: {ex.Message}");
            // 작업 전체가 오류로 끝났다(엔진이 안 뜸·모델 없음 등) — 도는 중이던 행이 '엔진 깨우는 중 10%'로 남지 않게(멈췄을 때와 같다). 까닭은 상태 줄에 있다
            foreach (var r in _rows)
                if (!r.Done && !r.Failed && r.Started)
                { r.Stage = Loc.T("멈춤 — 오류로 작업이 끝났다냥"); r.Percent = 0; }
        }
        finally
        {
            _cts = null;
            _runner = null;
            _runPathSettings = null;
            BtnRun.IsEnabled = true;
            BtnStop.IsEnabled = false;
            RefreshWaitingUi();
            RefreshEditedUi();   // 작업 도중 끝난 장의 마스크를 고쳐 둔 것이 있으면 이제 한꺼번에 돌릴 수 있다
            jobDone.TrySetResult();
        }
    }

    // ── 작업 중에 닫기 (2026-11-13 점검 8번) ──
    // 전에는 묻지 않고 닫혔다: 프로세스가 끝나도 ComfyUI에 넣어 둔 생성은 끝까지 돌았다(멈추면 거두는 것은 [멈추기]의 길에만 있다).
    // 묻고, 멈춘 뒤(그 길이 서버의 프롬프트를 거둔다) 닫는다.
    private TaskCompletionSource? _jobDone;   // 도는 작업([시작]·묶음 다시 렌더링)이 뒷정리까지 끝나면 완료된다
    private bool _closeAsking, _closeAfterStop;

    /// <summary>작업([시작]·묶음 다시 렌더링)이 도는 중인가 — 설정 창의 [삭제]처럼 앱을 끝내는 길이 먼저 묻는다.</summary>
    internal bool IsJobRunning => _cts != null;

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_cts == null || _closeAfterStop) { base.OnClosing(e); return; }
        e.Cancel = true;            // 첫 await 전에 — 그 뒤에 바꾸면 창은 이미 닫혔다
        if (_closeAsking) return;   // 이미 묻는 중이거나 멈추는 중
        _closeAsking = true;
        try
        {
            var answer = System.Windows.MessageBox.Show(this, Loc.T("작업이 도는 중이다냥. 멈추고 닫을까냥?\n\n끝난 장은 남고, 남은 장은 다음에 이어서 할 수 있다냥."), "NekoManga",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (_cts == null)
            {
                // 묻는 동안 작업이 끝났다(메시지 상자는 화면 처리를 계속 돌린다) — 멈출 것이 없다. 아직 이 닫기 요청 안(await 전)이라 '예'면 그대로 닫히게 둔다.
                // 여기서 Close()를 다시 부르면 닫히는 중의 창을 또 닫는 것이라 WPF가 던진다(2026-11-14 검수)
                if (answer == System.Windows.MessageBoxResult.Yes) { e.Cancel = false; base.OnClosing(e); }
                return;
            }
            if (answer != System.Windows.MessageBoxResult.Yes) return;
            var done = _jobDone?.Task;
            _cts.Cancel();
            TxtStatus.Text = Loc.T("멈추는 중이다냥 — 곧 닫힌다냥");
            IsEnabled = false;
            // 멈춤은 단계 경계에서 걸린다(돌기 시작한 기본 모델 추론은 끝까지 간다) — 서버의 프롬프트를 거두는 데는 1초 안쪽(실측 0.6~0.8초). 오래 걸리면 기다리지 않고 닫는다.
            // 작업은 아직 돌고 있으니(위에서 확인) 이 기다림은 반드시 한 번 물러났다 돌아온다 — 아래 Close()는 처음의 닫기 요청 밖에서 불린다
            if (done != null) await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(15)));
            IsEnabled = true;   // 닫기가 어떤 까닭으로든 안 되면 꺼진 창으로 남지 않게
            _closeAfterStop = true;
            Close();
        }
        finally { _closeAsking = false; }
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    /// <summary>검토 대기 중인 장이 있을 때만 [대기 중 전부 진행] 버튼을 보인다.</summary>
    private void RefreshWaitingUi()
    {
        int n = _runner?.WaitingFiles.Count ?? 0;
        BtnProceedAll.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (n > 0) BtnProceedAll.Content = Loc.F($"대기 중 {n}장 전부 진행");
    }

    private void BtnProceedAll_Click(object sender, RoutedEventArgs e)
    {
        int n = _runner?.ConfirmAll() ?? 0;
        TxtStatus.Text = n > 0 ? Loc.F($"검토 대기 {n}장을 지금 마스크 그대로 검열 제거로 보냈다냥") : Loc.T("검토 대기 중인 장이 없다냥");
        RefreshWaitingUi();
    }

    private bool IsWaiting(string file) => _runner?.WaitingFiles.Contains(file, StringComparer.OrdinalIgnoreCase) == true;

    // ── 고친 장 모두 다시 렌더링 (2026-11-13 사용자 요청: "수정이 된 페이지들을 전체 한번에 다시 재랜더링", 끝나면 또 고쳐 반복) ──
    // 결과 창에서 끝난 장의 마스크를 고쳐 저장하면 사이드카에 표시(MaskSidecar.Pending)가 남는다. 그 장들만 검열 제거 → 식자를 다시 돌린다 —
    // ⚙ 설정의 검열 제거 방식 그대로(작업과 같게), 채색은 다시 하지 않는다(수정 창 결정 2026-11-11: 채색된 장은 채색된 그림 위에서 지운다).
    // 표시는 러너가 그 마스크로 결과물을 쓴 뒤에만 지우므로, 멈추거나 실패한 장은 다음에 다시 모인다.
    private int _editedGen;

    /// <summary>고친 마스크가 아직 결과물에 반영되지 않은 행들 — 행마다 사이드카 json을 읽으므로 백그라운드에서 찾는다.</summary>
    private async Task<List<(FileRow Row, string OutPath)>> FindEditedRowsAsync()
    {
        var slots = _rows.Select(r => (Row: r, OutPath: OutPathOf(r))).ToList();   // OutPathOf는 행과 설정을 읽는다 — UI 스레드에서 먼저
        return await Task.Run(() => slots.Where(x => MaskSidecar.IsPendingFor(x.OutPath, x.Row.Path)).ToList());
    }

    /// <summary>고쳐 둔 장이 있고 작업이 돌지 않을 때만 [고친 N장 다시 렌더링] 버튼을 보이고, 그 행들에 ✎를 붙인다.
    /// 작업이 도는 동안에는 훑지 않는다(러너가 쓰는 사이드카를 읽다 잠깐이라도 쥐지 않게) — 끝나면 다시 센다.</summary>
    private async void RefreshEditedUi()
    {
        int gen = ++_editedGen;
        if (_cts != null) { BtnRerenderEdited.Visibility = Visibility.Collapsed; return; }
        HashSet<FileRow> edited;
        try { edited = (await FindEditedRowsAsync()).Select(x => x.Row).ToHashSet(); }
        catch { edited = []; }
        if (gen != _editedGen) return;   // 그사이 목록·작업 상태가 또 바뀌었다 — 그쪽 요청이 다시 센다
        foreach (var r in _rows) r.Edited = edited.Contains(r);
        BtnRerenderEdited.Visibility = edited.Count > 0 && _cts == null ? Visibility.Visible : Visibility.Collapsed;
        if (edited.Count > 0) BtnRerenderEdited.Content = Loc.F($"고친 {edited.Count}장 다시 렌더링");
    }

    private void BtnRerenderEdited_Click(object sender, RoutedEventArgs e) => _ = RerenderEditedAsync();

    /// <param name="handed">결과 창이 닫히며 넘겨 준 고친 장들 — 훑기가 못 읽은 장을 메운다.</param>
    private async Task RerenderEditedAsync(IReadOnlyList<(string Source, string OutPath)>? handed = null)
    {
        if (_cts != null) return;   // 작업이나 다른 묶음이 도는 중
        if (_updating) { TxtStatus.Text = Loc.T("업데이트를 받는 중이다냥 — 곧 앱이 다시 시작된다냥"); return; }
        var s = App.Settings.ShallowClone();   // 러너는 만들 때의 설정을 박제한다 — 전역 설정은 건드리지 않는다
        s.EnableAutoColor = false;             // 채색은 다시 하지 않는다 — 채색된 장은 Core가 base(채색 뒤·식자 전)에서 지운다(DecensorAndLetterOnly)
        s.EnableDecensor = true;
        if (s.DecensorMode is "ComfyRegen" or "ComfyCustom" && !s.ComfyAvailable)
        {
            TxtStatus.Text = Loc.T("재생성·커스텀은 ComfyUI가 필요하다냥 — 설정 → 구성요소에서 설치해 줘냥");
            RefreshEditedUi();   // 결과 창에서 방금 고친 장이 버튼·✎에 보이게
            return;
        }
        var cts = _cts = new CancellationTokenSource();   // 먼저 잡는다 — 아래에서 대상을 찾는 사이 [시작]이나 또 한 번의 클릭이 끼어들지 않게
        var jobDone = _jobDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runPathSettings = s;   // 도는 동안 설정 창에서 출력 폴더를 바꿔도 행의 결과 경로가 흔들리지 않게 ([시작]과 같다)
        ++_editedGen;
        BtnRun.IsEnabled = false;
        BtnStop.IsEnabled = true;
        BtnRerenderEdited.Visibility = Visibility.Collapsed;
        // 상태 글과 함께 플래그도 들었다 돌려놓는다 — 전에는 글로 가렸으니(Stage != "대기"·"실패"로 시작) 글이 바뀌면 판정도 같이 바뀌었다
        var before = new Dictionary<FileRow, (string Stage, double Percent, bool Started, bool Failed)>();
        var finished = new HashSet<FileRow>();
        // 끝나지 않은 장은 지난 결과물·상태 그대로 — 고친 마스크 표시가 남아 다시 누르면 이어서 한다
        int RestoreUnfinished()
        {
            int left = 0;
            foreach (var (row, was) in before)
                if (!finished.Contains(row)) { row.Stage = was.Stage; row.Percent = was.Percent; row.Started = was.Started; row.Failed = was.Failed; left++; }
            return left;
        }
        try
        {
            var edited = await FindEditedRowsAsync();
            if (handed is { Count: > 0 })
            {
                // 결과 창이 넘겨 준 장: 방금 저장한 사이드카를 백신·동기화가 잠깐 쥐고 있으면 위의 훑기가 못 읽어 그 장이 말없이 빠진다(2026-11-13 검수).
                // 읽어서 '표시 없음'이 확인된 장(그사이 다른 길로 반영됨)만 빼고 넣는다 — 못 읽은 장은 러너가 잠김을 기다렸다 읽고, 끝내 못 읽으면 그 장의 실패로 알린다
                var readable = await Task.Run(() => handed.Where(h => MaskSidecar.Load(h.OutPath) is not { Pending: false }).ToList());
                var have = edited.Select(x => x.Row).ToHashSet();
                foreach (var h in readable)
                    if (_rows.FirstOrDefault(r => string.Equals(r.Path, h.Source, StringComparison.OrdinalIgnoreCase)) is { } row && have.Add(row))
                        edited.Add((row, h.OutPath));
                edited = [.. edited.OrderBy(x => _rows.IndexOf(x.Row))];
            }
            if (edited.Count == 0) { TxtStatus.Text = Loc.T("마스크를 고친 장이 없다냥"); return; }
            // ✎는 여기서도 붙인다 — 결과 창 버튼으로 바로 온 장은 목록을 훑은 적이 없어 표시 없이 '다시 렌더링 대기'로 돌았다(2026-11-13 실제 흐름에서 봄)
            foreach (var (row, _) in edited)
            {
                before[row] = (row.Stage, row.Percent, row.Started, row.Failed);
                row.Stage = Loc.T("다시 렌더링 대기"); row.Started = true; row.Failed = false;   // 이제 '대기'도 '실패 — …'도 아닌 글이다
                row.Percent = 0; row.Edited = true;
            }
            TxtStatus.Text = Loc.F($"고친 {edited.Count}장 다시 렌더링 중이다냥... (검열 제거 + 식자)");   // 상태 줄은 기본 폭에서 앞 20자쯤만 보인다 — 중요한 말을 앞에

            var progress = new Progress<PageProgress>(p =>
            {
                var hit = edited.FirstOrDefault(x => string.Equals(x.Row.Path, p.File, StringComparison.OrdinalIgnoreCase));
                if (hit.Row is not { } row) return;
                if (p.Error != null)
                {
                    row.Stage = Loc.F($"다시 렌더링 실패 — {p.Error} (지난 결과물은 그대로다냥)");
                    row.Percent = before[row].Percent;
                    finished.Add(row);
                    return;
                }
                row.Stage = p.Stage;
                if (p.Percent >= 0) row.Percent = p.Percent;   // 경고 보고(-1)는 진행률을 건드리지 않는다
                if (p.Done)
                {
                    // 행의 Done은 건드리지 않는다 — '이번 [시작]이 끝낸 장'이라는 뜻이라, 지난 작업의 결과물을 고쳐 돌린 장을 Done으로 올리면 다음 [시작]이 그 장을 건너뛴다(낱장 [지우기]도 올리지 않는다)
                    row.Edited = false;
                    finished.Add(row);
                    ShowResultFor(row);
                }
            });

            using var runner = new PipelineRunner(s);
            var pages = edited.Select(x => (x.Row.Path, x.OutPath)).ToList();
            var options = new PipelineRunner.RedoPageOptions(DecensorAndLetterOnly: true);
            var (done, failed) = await Task.Run(() => runner.RedoPagesAsync(pages, progress, cts.Token, options));
            TxtStatus.Text = failed == 0
                ? Loc.F($"고친 {done}장 다시 렌더링 완료다냥 — 더블클릭해 확인하라냥")
                : Loc.F($"다시 렌더링 — 성공 {done}장 / 실패 {failed}장 (실패한 장은 지난 결과물 그대로다냥)");
        }
        catch (OperationCanceledException)
        {
            int left = RestoreUnfinished();
            TxtStatus.Text = Loc.F($"멈췄다냥 — 남은 {left}장은 그대로다냥 ([고친 장 다시 렌더링]으로 이어서 한다냥)");
        }
        catch (Exception ex)
        {
            RestoreUnfinished();
            TxtStatus.Text = Loc.F($"오류: {ex.Message}");
        }
        finally
        {
            _cts = null;
            _runPathSettings = null;
            BtnRun.IsEnabled = true;
            BtnStop.IsEnabled = false;
            RefreshWaitingUi();
            RefreshEditedUi();
            jobDone.TrySetResult();
        }
    }

    private void BtnOpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var dir = !string.IsNullOrWhiteSpace(App.Settings.OutputRoot) ? App.Settings.OutputRoot : _lastOutputDir;
        if (dir != null && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        else
            TxtStatus.Text = Loc.T("아직 결과물이 없다냥");
    }

    /// <summary>목록에서 고른 원고의 결과물을 띄운다. 자동 감지가 놓친 검열을 거기서 직접 지울 수 있다.
    /// 결과 파일이 없으면(아직 안 돌렸거나 검열이 없어 원본이 그대로 나간 경우) 원본을 연다.</summary>
    private void OpenResultFor(FileRow? row)
    {
        row ??= _rows.FirstOrDefault();
        if (row == null)
        {
            TxtStatus.Text = Loc.T("먼저 이미지를 올려 줘냥");
            return;
        }
        // 업데이트를 다 받으면 앱이 곧장 다시 시작된다 — 그사이 수정 창에서 [지우기]를 돌리거나 고치던 것이 말없이 끊기지 않게
        if (_updating) { TxtStatus.Text = Loc.T("업데이트를 받는 중이다냥 — 곧 앱이 다시 시작된다냥"); return; }

        // 창 안에서 ◀ ▶로 넘겨볼 수 있게 목록 전체를 넘긴다 (결과물 경로도 함께 — .neko 사이드카 위치)
        var pages = _rows.Select(r => new ResultWindow.PageRef(r.ViewPath, OutPathOf(r), r.Path)).ToList();
        var win = new ResultWindow(pages, Math.Max(0, _rows.IndexOf(row)),
            confirmPage: f => { var ok = _runner?.ConfirmPage(f) == true; if (ok) RefreshWaitingUi(); return ok; },
            isWaiting: IsWaiting,
            startOnMask: IsWaiting(row.Path),
            isDone: f => _rows.Any(r => r.Done && string.Equals(r.Path, f, StringComparison.OrdinalIgnoreCase)),
            confirmAll: () =>
            {
                int n = _runner?.ConfirmAll() ?? 0;
                RefreshWaitingUi();
                if (n > 0) TxtStatus.Text = Loc.F($"검열 제거를 시작했다냥 — {n}장 진행 중");
                return n;
            },
            isJobRunning: () => _cts != null,
            canRerenderEdited: true) { Owner = this };
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
        // 창에서 [고친 장 모두 다시 렌더링]을 눌렀으면 여기서 돌린다(창은 닫혔고 진행은 행이 보여 준다). 그냥 닫았으면 고쳐 둔 장 수만 버튼에 다시 센다
        if (win.RerenderEditedRequested) _ = RerenderEditedAsync(win.RerenderEditedPages);
        else RefreshEditedUi();
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
        NoteSettingsApplyNextJob();
    }

    // ── 업데이트 ──────────────────────────────────────────────
    private bool _updating;   // 업데이트를 받는 중 — 다 받으면 앱이 바로 다시 시작되므로 그사이 작업을 시작하지 못하게 한다
    private async Task CheckUpdateAsync()
    {
        try
        {
            _pendingUpdate = await _update.CheckAsync();
            if (_pendingUpdate != null)
            {
                BtnUpdate.Visibility = Visibility.Visible;
                var version = _pendingUpdate.TargetFullRelease.Version;
                UiText.Bind(BtnUpdate, System.Windows.Controls.ContentControl.ContentProperty, () => Loc.F($"🎀 v{version} 업데이트!"));
            }
        }
        catch { /* 오프라인 등 — 조용히 넘어간다 */ }
    }

    private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate == null) return;
        // 적용하면 앱이 곧장 꺼졌다 다시 켜진다 — 도는 작업이 말없이 끊기고 엔진의 생성은 끝까지 돈다(창 닫기와 같은 길, 2026-11-13 점검 8번)
        if (_cts != null) { TxtStatus.Text = Loc.T("작업이 도는 중이다냥 — 업데이트는 끝난 뒤에 해 줘냥 (앱이 다시 시작된다냥)"); return; }
        BtnUpdate.IsEnabled = false;
        _updating = true;
        try
        {
            await _update.DownloadAndApplyAsync(_pendingUpdate,
                p => Dispatcher.Invoke(() => TxtStatus.Text = Loc.F($"업데이트 다운로드 {p}%")));
        }
        catch (Exception ex)
        {
            TxtStatus.Text = Loc.F($"업데이트 실패: {ex.Message}");
            BtnUpdate.IsEnabled = true;
        }
        finally { _updating = false; }
    }
}
