using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NekoManga.Core;
using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;
using OpenCvSharp;
// UseWindowsForms가 켜져 있어 Brush·MouseEventArgs 등이 WinForms와 겹친다 — 별칭을 유지할 것.
using WinPoint = System.Windows.Point;
using CvPoint = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextRenderer = NekoManga.Core.Pipeline.TextRenderer;
using TextBox = System.Windows.Controls.TextBox;
using FontFamily = System.Windows.Media.FontFamily;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;

namespace NekoManga.App;

// 페이지 — 디스크에서 읽기, 이웃 장 미리 읽기, 페이지 전환(LoadPage)과 넘김 버튼
public partial class ResultWindow
{
    // ── 페이지 ──────────────────────────────────────────────
    /// <summary>디스크에서 읽은 한 페이지 묶음(결과물 + 식자 사이드카 + 식자 전 원고). 읽기는 백그라운드.</summary>
    private sealed record PageData(Mat Img, LetterSidecar? Sidecar, Mat? Base,
        MaskSidecar? MaskSc = null, Mat? Mask = null, Mat? Pre = null, Mat? PreDisp = null, DiskStamp Stamp = default, string ViewPath = "")
    {
        public void Dispose() { Img.Dispose(); Base?.Dispose(); Mask?.Dispose(); Pre?.Dispose(); PreDisp?.Dispose(); }
    }

    /// <summary>원본 원고 경로 — Source가 비어 있으면(--result 모드) ViewPath.</summary>
    private static string SourceOf(PageRef r) => string.IsNullOrEmpty(r.Source) ? r.ViewPath : r.Source;

    /// <summary>검열이 없어 원본을 확장자 그대로 복사한 결과물 경로 — 러너 규칙 그대로. 확장자는 원본(Source)에서 — ViewPath는 결과물로 바뀔 수 있다.</summary>
    private static string AsIsOf(PageRef r) => PipelineRunner.AsIsPathFor(r.OutPath, SourceOf(r));

    /// <summary>지금 보여 줄 파일 — 이 원고의 결과물이 있으면 결과물(창을 연 뒤 생겼든 지난 작업 것이든), 없으면 보던 파일.
    /// 결과 이름은 원고 하나로 정해지므로 그 자리의 결과물은 이 원고 것이다. --result 모드(원본 모름)는 보던 파일 그대로.
    /// 저장도 결과물 자리에 하므로, 끝난 장을 고쳐 원본 파일에 덮어쓰는 일이 없다.</summary>
    private static string ResolveView(PageRef r)
    {
        if (string.IsNullOrEmpty(r.OutPath) || string.IsNullOrEmpty(r.Source)) return r.ViewPath;
        var p = PipelineRunner.ResolveResultPath(r.OutPath, SourceOf(r));   // 메인 창 ResolveViewPath와 같은 규칙 (Core 한 곳)
        if (!string.Equals(p, SourceOf(r), StringComparison.OrdinalIgnoreCase)) return p;
        return File.Exists(r.ViewPath) ? r.ViewPath : p;   // 결과물이 없다 — 보던 파일(지워졌으면 원본)
    }

    /// <summary>이 페이지가 디스크에서 바뀌었는지 가리는 도장 — Result: 결과물 파일들(OutPath·원본 그대로 복사본)만, All: 보던 파일·사이드카까지 전부(이름으로 나눠 위치에 기대지 않는다).
    /// 미리 읽어 둔 이웃 장이 그 사이 '검토 대기'가 되거나(러너가 마스크 사이드카를 씀) 작업이 끝나 결과물이 생기면 All이 달라져 캐시를 버리고 다시 읽는다.
    /// 해시로 접지 않고 값을 그대로 이어 붙인다 — 32비트 해시는 바뀐 파일이 같은 값으로 떨어질 수 있다. 기본값(빈 문자열)은 '모름 = 다시 읽어라'.</summary>
    private readonly record struct DiskStamp(string Result, string All)
    {
        public bool IsEmpty => string.IsNullOrEmpty(All);
    }

    private static DiskStamp StampOf(PageRef r)
    {
        if (string.IsNullOrEmpty(r.OutPath)) return default;   // 빈 자리표(페이지 없음) — 사이드카 경로를 만들 수 없다
        static string Stat(string p)
        {
            try { var fi = new FileInfo(p); return fi.Exists ? $"{fi.LastWriteTimeUtc.Ticks}:{fi.Length};" : "-;"; }
            catch { return "!;"; }
        }
        var result = Stat(r.OutPath) + Stat(AsIsOf(r));
        var rest = Stat(r.ViewPath) + Stat(LetterSidecar.JsonPathFor(r.OutPath)) + Stat(LetterSidecar.BasePathFor(r.OutPath))
                 + Stat(MaskSidecar.JsonPathFor(r.OutPath)) + Stat(MaskSidecar.MaskPathFor(r.OutPath)) + Stat(MaskSidecar.PrePathFor(r.OutPath));
        return new DiskStamp(result, result + rest);
    }
    private readonly Dictionary<int, Task<PageData>> _prefetch = [];   // 이웃 페이지 미리 읽기 (2026-10-21: 페이지 넘김 지연 해소)
    private int _loadGen;

    private const int MaskDispMax = 1600;

    private static PageData ReadPage(PageRef r)
    {
        var stamp = StampOf(r);   // 읽기 시작 시점에 찍는다 — 읽는 동안 바뀐 것은 다음 비교에서 잡힌다 (경합에 져 캐시에 남는 결과도 있어 늘 찍는다)
        var path = ResolveView(r);
        var img = File.Exists(path) ? ImgIo.Read(path, ImreadModes.Color) : new Mat();
        if (img.Empty() && !string.Equals(path, r.ViewPath, StringComparison.OrdinalIgnoreCase))
        {
            // 결과물을 못 읽었다(쓰는 중이거나 깨짐) — 보여 주던 파일로. 빈 도장 = 다음 방문 때 다시 읽어라
            img.Dispose(); path = r.ViewPath; stamp = default;
            img = File.Exists(path) ? ImgIo.Read(path, ImreadModes.Color) : new Mat();
        }
        if (img.Empty()) return new PageData(img, null, null, Stamp: stamp, ViewPath: path);
        var sc = LetterSidecar.Load(r.OutPath);                   // 번역을 돌린 페이지에만 있다
        // 사이드카는 **이 원고의** 것만 — 남의(다른 화) 마스크·식자 블록을 보여 주거나 [다시 식자]로 구워 넣지 않는다 (폴더를 옮긴 경우는 이름+길이/크기로 허용)
        MaskSidecar? msc = MaskSidecar.Load(r.OutPath);   // 아래 마스크 탭도 이것을 쓴다
        if (msc != null && !string.IsNullOrEmpty(r.Source) && !msc.MatchesSource(r.Source)) msc = null;
        (int, int)? dims = msc != null ? (msc.SrcW, msc.SrcH) : (img.Width, img.Height);
        if (sc != null && !string.IsNullOrEmpty(r.Source) && !sc.BelongsTo(r.Source, dims))
        {
            // 길이 기록이 없는 옛 사이드카: 보던 그림은 채색으로 크기가 달라졌을 수 있으니 원고 파일의 실제 크기로 한 번 더
            (int, int)? srcDims = null;
            if (sc.SrcLen == 0 && msc == null)
                try { if (File.Exists(r.Source)) { using var si = ImgIo.Read(r.Source); if (!si.Empty()) srcDims = (si.Width, si.Height); } } catch { }
            if (srcDims == null || !sc.BelongsTo(r.Source, srcDims)) sc = null;
        }
        var b = sc != null ? ImgIo.Read(sc.BasePath) : null;
        if (b is { } bm && bm.Empty()) { bm.Dispose(); b = null; }

        // 검열 마스크 사이드카 — 검열 제거를 켜고 돌린 페이지에만 있다. 표시용 축소본(긴 변 1600)도 미리 만든다
        Mat? mask = null, pre = null, preDisp = null;
        if (msc != null)
        {
            mask = MaskSidecar.LoadMask(r.OutPath);
            if (MaskSidecar.HasPre(r.OutPath)) pre = ImgIo.Read(MaskSidecar.PrePathFor(r.OutPath), ImreadModes.Color);
            if (pre is { } pm && pm.Empty()) { pm.Dispose(); pre = null; }
            if (mask != null && pre != null && mask.Size() != pre.Size()) { mask.Dispose(); mask = null; }
            if (mask == null || pre == null) { mask?.Dispose(); pre?.Dispose(); mask = pre = null; msc = null; }
            else
            {
                double ds = Math.Min(1.0, (double)MaskDispMax / Math.Max(pre.Width, pre.Height));
                preDisp = new Mat();
                Cv2.Resize(pre, preDisp, new Size((int)Math.Round(pre.Width * ds), (int)Math.Round(pre.Height * ds)), 0, 0, InterpolationFlags.Area);
            }
        }
        return new PageData(img, sc, b, msc, mask, pre, preDisp, stamp, path);
    }

    private Task<PageData> FetchPage(int i) => FetchPage(i, out _);

    /// <param name="cached">이미 미리 읽어 둔 것을 돌려줬는지 — 그때만 디스크 도장 비교가 의미 있다.</param>
    private Task<PageData> FetchPage(int i, out bool cached)
    {
        cached = _prefetch.TryGetValue(i, out var t);
        if (!cached)
        {
            var p = _refs[i];
            t = Task.Run(() => ReadPage(p)); _prefetch[i] = t;
        }
        return t!;
    }

    /// <summary>미리 읽어 둔 이 페이지를 버린다 — 디스크의 결과물·사이드카가 바뀐 뒤 다시 읽게.</summary>
    private void InvalidatePrefetch(int i)
    {
        if (_prefetch.Remove(i, out var t))
            t.ContinueWith(x => { if (x.Status == TaskStatus.RanToCompletion) x.Result.Dispose(); });
    }

    /// <summary>현재 페이지의 양옆을 미리 읽어 둔다 — ◀ ▶로 넘길 때 디스크·PNG 디코딩을 기다리지 않게. 멀어진 것은 버린다.</summary>
    private void PrefetchNeighbors()
    {
        foreach (var k in _prefetch.Keys.Where(k => Math.Abs(k - _index) > 1).ToList())
        {
            var t = _prefetch[k]; _prefetch.Remove(k);
            t.ContinueWith(x => { if (x.Status == TaskStatus.RanToCompletion) x.Result.Dispose(); });
        }
        if (_index > 0) FetchPage(_index - 1);
        if (_index < _refs.Count - 1) FetchPage(_index + 1);
    }

    /// <summary>페이지 전환. 읽기는 백그라운드(미리 읽어 둔 것이 있으면 즉시) — 읽는 동안은 넘김 버튼만 잠근다.
    /// 2026-10-21 이전엔 UI 스레드에서 PNG 두 장(결과물·식자 전 원고)을 동기로 읽어 큰 페이지에서 0.5초쯤 멈췄다.</summary>
    private bool _loading;   // 페이지 읽기 대기 중 — 그동안 [지우기] 적용은 막는다 (옛 _img를 지우는 사이 복원이 그 위에 쓰면 안 된다)

    private async void LoadPage(int idx, bool fit)
    {
        var statusAfter = _statusAfterLoad; _statusAfterLoad = null;   // 이 호출의 안내 — 일찍 끝나면(경합·취소) 버린다, 다른 페이지에 새지 않게
        if (_busy) return;                 // 복원이 돌고 있으면 _img를 건드리지 않는다
        if (!ConfirmDiscard()) return;
        StashRegenFields();                // 떠나는 페이지의 재생성 값을 기억
        int gen = ++_loadGen;

        idx = Math.Clamp(idx, 0, _refs.Count - 1);
        _index = idx;
        _loading = true;
        TxtFile.Text = System.IO.Path.GetFileName(_refs[idx].ViewPath);
        TxtFile.ToolTip = _refs[idx].ViewPath;
        TxtCount.Text = $"{idx + 1} / {_refs.Count}";
        BtnPrev.IsEnabled = BtnNext.IsEnabled = false;
        if (CmbPages.SelectedIndex != idx) CmbPages.SelectedIndex = idx;

        var task = FetchPage(idx, out bool cached);
        if (!task.IsCompleted) TxtStatus.Text = "읽는 중...";
        PageData data; string? readError = null;
        try { data = await task; }
        catch (Exception ex) { data = new PageData(new Mat(), null, null); readError = ex.Message; }
        // 기다리는 사이 또 넘겼거나 창이 닫혔다 — 결과는 사전(_prefetch)이 계속 소유하므로 여기서 해제·제거하지 않는다
        // (지는 쪽이 이기는 쪽 키를 지우고 Mat을 해제해 ObjectDisposedException으로 죽던 경로)
        if (gen != _loadGen) return;
        // 도장 비교는 미리 읽어 둔 것(cached)에만 — 방금 읽은 것은 달라질 수 없다. 파일 stat 여덟 번이라 느린 드라이브에서 UI를 막지 않게 백그라운드에서
        DiskStamp? nowStamp = null;
        if (cached && readError == null) { var pr = _refs[idx]; nowStamp = await Task.Run(() => StampOf(pr)); if (gen != _loadGen) return; }
        if (nowStamp is { } now && (data.Stamp.IsEmpty || data.Stamp.All != now.All))
        {
            // 미리 읽어 둔 뒤 디스크가 바뀌었다(이웃 장이 '검토 대기'가 됐거나 작업이 끝남) — 캐시를 버리고 다시 읽는다.
            // 이 시점엔 우리가 최신 LoadPage(gen 일치)라 사전에서 뺀 결과는 우리만 쥔다.
            _prefetch.Remove(idx);
            data.Dispose();
            var pr2 = _refs[idx];
            var again = Task.Run(() => ReadPage(pr2));
            _prefetch[idx] = again;
            try { data = await again; }
            catch (Exception ex) { data = new PageData(new Mat(), null, null); readError = ex.Message; }
            if (gen != _loadGen) return;
        }
        _loading = false;
        _prefetch.Remove(idx);
        // 읽기에 실패한 경로(아직 쓰는 중인 결과물 등)로는 갈아타지 않는다 — 원래 보여 주던 파일을 유지
        if (!data.Img.Empty() && !string.IsNullOrEmpty(data.ViewPath) && !string.Equals(data.ViewPath, _refs[idx].ViewPath, StringComparison.OrdinalIgnoreCase))
        {
            // 창을 연 뒤 결과물이 생긴 장 — 보여 주는 파일과 저장 대상을 결과물로 바꾼다
            bool previousSession = _isDone?.Invoke(SourceOf(_refs[idx])) != true;   // 메인 창은 아직 원본('대기'·'검토 대기')인데 지난 작업의 결과물을 보여 주는 경우
            SetViewPath(idx, data.ViewPath);
            if (previousSession) statusAfter ??= "지난 작업의 결과물을 보여 주고 있어요 — 이 장을 다시 돌리면 새 결과물로 바뀝니다";
        }

        _img.Dispose();
        while (_undo.Count > 0) _undo.Pop().Dispose();
        _dirty = false;
        BtnSave.IsEnabled = false;
        BtnUndo.IsEnabled = false;
        InvalidateBitmap();
        ResetMaskState(data);

        _img = data.Img;
        BtnPrev.IsEnabled = _index > 0;
        BtnNext.IsEnabled = _index < _refs.Count - 1;
        PrefetchNeighbors();

        if (_img.Empty())
        {
            TxtStatus.Text = "이미지를 읽지 못했어요: " + Path.GetFileName(_refs[_index].ViewPath) + (readError != null ? " — " + readError : "");
            ImgView.Source = null;
            Overlay.Children.Clear();
            return;
        }
        _sidecar = data.Sidecar;
        _letterBlocks = _sidecar?.Blocks.ToList() ?? [];
        _baseImg?.Dispose();
        _baseImg = data.Base;
        _letterPreview = false;
        TabLetter.IsEnabled = _sidecar != null && _baseImg != null;
        if (!TabLetter.IsEnabled && TabMode.SelectedItem == TabLetter) TabMode.SelectedIndex = 0;
        TabDecensor.IsEnabled = _maskMat != null && _maskPre != null;   // ResetMaskState가 저장 마스크 없는 장에도 빈 마스크를 만든다 — 그림을 못 읽은 장만 꺼진다
        if (!TabDecensor.IsEnabled && TabMode.SelectedItem == TabDecensor) TabMode.SelectedIndex = 1;
        // 검토 대기 페이지(또는 마스크부터 열라는 요청)는 검열 지우기 탭의 마스크 보기부터 보여 준다
        if (TabDecensor.IsEnabled && (_startOnMask || IsWaitingPage)) { TabMode.SelectedItem = TabDecensor; RbViewMask.IsChecked = true; }
        _startOnMask = false;
        RefreshBlockCombo();
        UpdateMaskInfo();
        UpdateModeButtons();
        RefreshRegionPreview();

        if (fit) FitToWindow();
        Redraw();
        if (RegenMode) LoadRegenFields();   // 새 페이지의 재생성 값(기억된 것 또는 설정값)
        TxtStatus.Text = statusAfter ?? (IsWaitingPage ? "검토 대기 중 — 빨간 마스크를 고친 뒤 [검열 제거 진행]을 누르세요"
                       : MaskMode ? (HasStoredMask ? "빨간 마스크를 고친 뒤 [지우기] — 이 장만 검열 제거·식자를 다시 해요" : "지울 곳을 칠한 뒤 [지우기] — 결과물에 바로 지워요")
                       : "결과 보기 — 지울 곳을 칠하려면 [🎭 마스크 보기]");
    }

    /// <summary>LoadPage가 끝난 뒤 상태 줄에 둘 안내 — 중간에 두면 LoadPage 끝의 기본 문구가 덮어쓴다 (다시 돌리기 완료·지난 결과물 표시).</summary>
    private string? _statusAfterLoad;

    private void BtnPrev_Click(object sender, RoutedEventArgs e) => LoadPage(_index - 1, fit: true);
    private void BtnNext_Click(object sender, RoutedEventArgs e) => LoadPage(_index + 1, fit: true);

    private void CmbPages_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || CmbPages.SelectedIndex < 0 || CmbPages.SelectedIndex == _index) return;
        LoadPage(CmbPages.SelectedIndex, fit: true);
    }
}
