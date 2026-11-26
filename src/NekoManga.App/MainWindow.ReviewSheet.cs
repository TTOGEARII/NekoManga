// 메인 창 — 번역 검수 표(CSV) 내보내기·들여오기(2026-11-26 사용자 "csv로 내보내서 한글을 고친 다음 다시 넣으면 일괄 수정", CarrotMangaTranslator의 검수 시트 참고).
// 내보내기: 목록의 장 가운데 식자 사이드카가 있는 장의 블록(결과 파일·번호·원문·번역)을 CSV(UTF-8 BOM — 엑셀이 한글을 바로 읽는다)로 쓴다.
// 들여오기: 결과 파일·번호로 블록을 찾아 번역 글만 바꾸고(ReviewSheet.Apply) 사이드카를 저장한 뒤 그 장을 지금 설정으로 다시 식자한다(--reletter와 같은 길 — 결과물을 제자리에 덮는다).
using System.IO;
using System.Text;
using System.Windows;
using NekoManga.Core;
using NekoManga.Core.Engine;
using NekoManga.Core.Pipeline;
using TextRenderer = NekoManga.Core.Pipeline.TextRenderer;   // UseWindowsForms의 전역 using(System.Windows.Forms.TextRenderer)과 겹친다

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
                        var next = sc with { Blocks = applied };
                        // 임시 파일에 먼저 그린다 — 못 그리면 사이드카도 결과물도 옛 것 그대로(둘이 어긋나지 않게). 사이드카에 박제한 원본 크기로 환산한다(원고를 옮겼어도 제자리에)
                        var tmp = Path.Combine(Path.GetTempPath(), $"neko-sheet-{Guid.NewGuid():N}.png");
                        try
                        {
                            TextRenderer.RenderBlocks(next.BasePath, next.Source, next.SrcW, next.SrcH, applied, tmp, settings, next.CommonSize);
                            next.Save(group.Key);   // 고친 글이 다음 다시 렌더링·결과 창에도 남게
                            File.Copy(tmp, group.Key, overwrite: true);
                        }
                        finally { try { File.Delete(tmp); } catch { } }
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

    /// <summary>다시 식자한 결과물을 보여 주는 행의 썸네일을 새로 읽는다(ViewPath가 결과물인 행만 — 원고를 보여 주는 행은 그대로).</summary>
    private void RefreshThumbsAfterReletter()
    {
        foreach (var r in _rows)
            if (!string.Equals(r.ViewPath, r.Path, StringComparison.OrdinalIgnoreCase)) _ = LoadThumbAsync(r);
    }
}
