// 메인 창 — 번역 검수 표(CSV) 내보내기·들여오기(2026-11-26 사용자 "csv로 내보내서 한글을 고친 다음 다시 넣으면 일괄 수정", CarrotMangaTranslator의 검수 시트 참고).
// 내보내기: 목록의 장 가운데 식자 사이드카가 있는 장의 블록(결과 파일·번호·원문·번역)을 CSV(UTF-8 BOM — 엑셀이 한글을 바로 읽는다)로 쓴다.
// 들여오기: 결과 파일·번호로 블록을 찾아 번역 글만 바꾸고(ReviewSheet.Apply) 사이드카를 저장한 뒤 그 장을 지금 설정으로 다시 식자한다(--reletter와 같은 길 — 결과물을 제자리에 덮는다).
// 화 전체 검수(2026-11-28 사용자 "2검수해줘"): 목록의 장 전부를 한 화로 보고 원문·번역을 LLM 번역기에 통째로 보내 흔들린 줄만 고쳐 받는다(ChapterReview) — 같은 메뉴, 같은 다시 식자 길.
//   설정 TranslateChapterReview를 켜면 [시작]한 작업이 끝날 때도 돈다(MainWindow.xaml.cs — 같은 ChapterReviewAsync).
using System.IO;
using System.Text;
using System.Windows;
using NekoManga.Core;
using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;

namespace NekoManga.App;

public partial class MainWindow
{
    /// <summary>[📑 번역 표] — 왼쪽 클릭에도 메뉴(내보내기/들여오기)를 편다(ContextMenu는 오른쪽 클릭에만 열린다).</summary>
    private void BtnReviewSheet_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } el) { menu.PlacementTarget = el; menu.IsOpen = true; }
    }

    private void ReviewExport_Click(object sender, RoutedEventArgs e)
    {
        var pages = new List<(string OutPath, IReadOnlyList<TextBlockInfo> Blocks)>();
        foreach (var row in _rows)
        {
            var outPath = OutPathOf(row);   // 작업을 돌린 설정의 출력 폴더를 따른다(메인 창의 결과 보기와 같은 길)
            try { if (LetterSidecar.Load(outPath) is { Blocks.Count: > 0 } sc) pages.Add((outPath, sc.Blocks)); }
            catch { }   // 잠긴·깨진 사이드카 — 그 장은 표에서 빠진다
        }
        if (pages.Count == 0) { TxtStatus.Text = Loc.T("내보낼 번역이 없다냥 — 식자까지 끝난 장이 없다냥"); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Loc.T("CSV 파일") + "|*.csv",
            FileName = Loc.T("번역 검수표") + ".csv",
            InitialDirectory = Path.GetDirectoryName(pages[0].OutPath),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, ReviewSheet.Write(pages), new UTF8Encoding(true));
            TxtStatus.Text = Loc.F($"번역 검수 표를 저장했다냥: {pages.Count}장 {pages.Sum(p => p.Blocks.Count)}블록 — 엑셀에서 '번역' 칸만 고쳐 들여오라냥");
        }
        catch (Exception ex) { TxtStatus.Text = Loc.T("검수 표 저장 실패다냥: ") + ex.Message; }
    }

    private async void ReviewImport_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) { TxtStatus.Text = Loc.T("작업이 도는 중이다냥 — 끝난 뒤에 적용해 줘냥"); return; }
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("CSV 파일") + "|*.csv|" + Loc.T("모든 파일") + "|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        List<ReviewSheet.Row> rows;
        try { rows = ReviewSheet.Parse(File.ReadAllText(dlg.FileName)); }
        catch (Exception ex) { TxtStatus.Text = Loc.T("검수 표를 읽지 못했다냥: ") + ex.Message; return; }
        if (rows.Count == 0) { TxtStatus.Text = Loc.T("검수 표에 번역 줄이 없다냥"); return; }

        TxtStatus.Text = Loc.T("검수 표를 적용하는 중이다냥…");
        // 적용하는 동안 [시작]·결과 창을 막는다 — 같은 결과물을 동시에 쓰거나, 결과 창이 옛 블록으로 덮어쓰지 않게(검수 2026-11-26)
        BtnReviewSheet.IsEnabled = false; BtnRun.IsEnabled = false; BtnViewResult.IsEnabled = false; BtnEditThis.IsEnabled = false;
        var settings = App.Settings.ShallowClone();   // 적용하는 동안 설정을 바꿔도 이 묶음은 한 설정으로
        int pages = 0, blocks = 0, missing = 0, failed = 0, mismatch = 0;
        var failures = new List<string>();
        try
        {
            await Task.Run(() =>
            {
                foreach (var group in rows.GroupBy(r => r.OutPath, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var sc = LetterSidecar.Load(group.Key);
                        if (sc == null) { missing++; continue; }
                        var (applied, changed, mismatched) = ReviewSheet.Apply(sc.Blocks, group);
                        mismatch += mismatched;
                        if (changed == 0) continue;
                        (sc with { Blocks = applied }).SaveAndRender(group.Key, settings);   // 임시 파일에 먼저 그린다 — 못 그리면 사이드카도 결과물도 옛 것 그대로
                        pages++; blocks += changed;
                    }
                    catch (Exception ex) { failed++; if (failures.Count < 3) failures.Add(Path.GetFileName(group.Key) + ": " + ex.Message); }
                }
            });
        }
        finally { BtnReviewSheet.IsEnabled = true; BtnRun.IsEnabled = true; BtnViewResult.IsEnabled = true; BtnEditThis.IsEnabled = true; }
        TxtStatus.Text = Loc.F($"검수 표 적용: {pages}장 {blocks}블록을 고쳐 다시 식자했다냥 (결과를 못 찾은 장 {missing}, 원문이 안 맞아 건너뛴 줄 {mismatch}, 실패 {failed})")
                       + (failures.Count > 0 ? " — " + string.Join(" / ", failures) : "");
        RefreshThumbsAfterReletter();
        ShowPreview(FileList.SelectedItem as FileRow);   // 큰 미리보기도 새 결과물로
    }

    /// <summary>[📑 번역 표] → 화 전체 검수 — 목록의 장 전부를 한 화로 본다. 도는 동안은 작업처럼 [시작]·결과 창을 막고 [멈추기]로 그만둘 수 있다
    /// (번역기의 답을 기다리는 동안만 멈춰진다 — 답을 받은 뒤의 다시 식자는 장마다 끝까지 간다).</summary>
    private async void ChapterReview_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) { TxtStatus.Text = Loc.T("작업이 도는 중이다냥 — 끝난 뒤에 해 줘냥"); return; }
        var cts = _cts = new CancellationTokenSource();
        var jobDone = _jobDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);   // 도는 중에 창을 닫으면 멈추고 닫는다(OnClosing)
        BtnReviewSheet.IsEnabled = false; BtnRun.IsEnabled = false; BtnViewResult.IsEnabled = false; BtnEditThis.IsEnabled = false;
        BtnStop.IsEnabled = true;
        try
        {
            TxtStatus.Text = await ChapterReviewAsync(_rows.Select(OutPathOf).ToList(), App.Settings.ShallowClone(), cts.Token);
            RefreshThumbsAfterReletter();
            ShowPreview(FileList.SelectedItem as FileRow);   // 큰 미리보기도 새 결과물로
        }
        catch (OperationCanceledException) { TxtStatus.Text = Loc.T("화 전체 검수를 멈췄다냥 — 결과는 그대로다냥"); }
        finally
        {
            _cts = null;
            BtnReviewSheet.IsEnabled = true; BtnRun.IsEnabled = true; BtnViewResult.IsEnabled = true; BtnEditThis.IsEnabled = true;
            BtnStop.IsEnabled = false;
            jobDone.TrySetResult();
        }
    }

    /// <summary>화 전체 검수가 도는 중 — 결과 창을 열지 못하게 막는다.</summary>
    private bool _chapterReviewing;

    /// <summary>화 전체 검수의 몸통 — 메뉴와 작업 끝(설정 TranslateChapterReview)이 같이 쓴다. 돌려주는 것은 상태 줄에 쓸 글이다.
    /// 번역기의 오류·제한 시간은 던지지 않고 그 글로 알린다(결과물은 그대로다 — 끝난 작업을 실패로 만들지 않는다). 사용자가 멈춘 것만 던진다.</summary>
    private async Task<string> ChapterReviewAsync(IReadOnlyList<string> outPaths, AppSettings settings, CancellationToken ct)
    {
        // 결과 창이 열려 있으면 하지 않는다(작업 끝의 자동 검수 — 작업 중에 연 창이 남아 있을 수 있다): 창은 제 블록을 들고 있어, 검수가 식자 정보를 바꾼 뒤에 창이 저장하면 고침이 말없이 사라지고 그림만 새 글로 남는다
        if (OwnedWindows.OfType<ResultWindow>().Any()) return Loc.T("화 전체 검수: 결과 창이 열려 있어 하지 않았다냥 — 창을 닫고 [📑 번역 표]에서 돌려 줘냥");
        // 지시문을 받는 번역기(DeepSeek·Grok)만 할 수 있다 — Google·DeepL이거나 키가 없으면 다른 번역기로 넘어가지 않고 알린다
        if (TranslatorFactory.Create(settings) is not IChatModel model)
            return Loc.T("화 전체 검수는 DeepSeek·Grok 번역기로만 된다냥 — 설정 → 번역에서 번역기와 API 키를 확인해 줘냥");
        var status = new Progress<string>(t => TxtStatus.Text = t);
        ChapterReview.Report r;
        _chapterReviewing = true;   // 도는 동안 결과 창을 열지 못하게(OpenResultFor) — 답을 기다리는 몇 분 사이에 고친 것과 검수의 고침이 서로를 덮지 않게
        try { r = await Task.Run(() => ChapterReview.RunOnResultsAsync(model, outPaths, settings, status, ct), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Loc.T("화 전체 검수를 못 했다냥 — 결과는 그대로다냥: ") + ex.Message; }
        finally { _chapterReviewing = false; }

        if (r.Lines == 0) return Loc.T("화 전체 검수: 검수할 번역이 없다냥 — 식자까지 끝난 장이 없다냥");
        if (r.Open == 0) return Loc.T("화 전체 검수: 고칠 수 있는 줄이 없다냥 — 내가 고친 글이거나, 이 기능 전에 번역한 결과다냥(다시 번역하면 된다냥)");
        string text = r.Changed > 0
            ? Loc.F($"화 전체 검수: {r.Pages}장 {r.Lines}줄 가운데 {r.Changed}줄을 고쳐 다시 식자했다냥 — 결과 창 식자 탭의 ↻ 표시에서 전의 글을 보고 되돌릴 수 있다냥")
            : r.TooMany ? Loc.T("화 전체 검수: 번역기가 줄을 너무 많이 고치려 해서 쓰지 않았다냥 — 결과는 그대로다냥")
            : r.Unread ? Loc.T("화 전체 검수: 번역기의 답을 읽지 못했다냥 — 결과는 그대로다냥. 다시 해 보라냥")
            : r.Failures.Count > 0 ? Loc.T("화 전체 검수: 고친 장을 다시 식자하지 못했다냥 — 결과는 그대로다냥")
            : Loc.F($"화 전체 검수: {r.Pages}장 {r.Lines}줄을 봤다냥 — 고칠 줄이 없다냥");
        if (r.Changed > 0 && r.TooMany) text += Loc.T(" · 일부는 너무 많이 고치려 해서 쓰지 않았다냥");
        if (r.Changed > 0 && r.Unread) text += Loc.T(" · 일부 답은 읽지 못했다냥");
        if (r.Rejected > 0) text += Loc.F($" · 못 쓰는 답 {r.Rejected}줄은 버렸다냥");
        if (r.Skipped > 0) text += Loc.F($" · 다른 언어로 번역된 {r.Skipped}장은 뺐다냥");
        if (r.Failures.Count > 0) text += Loc.F($" · 다시 식자하지 못한 장 {r.Failures.Count}: ") + string.Join(" / ", r.Failures.Take(3));
        return text;
    }

    /// <summary>다시 식자한 결과물을 보여 주는 행의 썸네일을 새로 읽는다(ViewPath가 결과물인 행만 — 원고를 보여 주는 행은 그대로).</summary>
    private void RefreshThumbsAfterReletter()
    {
        foreach (var r in _rows)
            if (!string.Equals(r.ViewPath, r.Path, StringComparison.OrdinalIgnoreCase)) _ = LoadThumbAsync(r);
    }
}
