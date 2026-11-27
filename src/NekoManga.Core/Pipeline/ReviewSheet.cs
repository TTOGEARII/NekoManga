using System.Text;
using NekoManga.Core.Engine;

namespace NekoManga.Core.Pipeline;

/// <summary>번역 검수 표(CSV, 2026-11-26 사용자 "csv로 내보내서 한글을 고친 다음 다시 넣으면 일괄 수정" — CarrotMangaTranslator의 검수 시트 참고).
/// 한 줄이 블록 하나: 결과 파일(식자 사이드카를 찾는 열쇠), 번호(그 장의 블록 번호, 1부터), 원문, 번역. 사용자는 엑셀에서 '번역' 칸만 고쳐 다시 들여온다(<see cref="Apply"/>).
/// 모든 칸을 따옴표로 감싸고(쉼표·줄바꿈·따옴표가 든 글), 읽을 때는 RFC 4180대로 따옴표 안의 쉼표·줄바꿈·""를 받으며 엑셀이 붙이는 BOM·CRLF도 받는다.</summary>
public static class ReviewSheet
{
    public sealed record Row(string OutPath, int Index, string Source, string Translation);

    /// <summary>머리 줄 — 파일 안의 글이라 화면 언어와 상관없이 한국어다(엑셀에서 보는 사람이 고칠 칸은 '번역').
    /// 다섯째 칸 '확인'은 읽기만 하는 칸이다(2026-11-27): 다른 번역기로 메운 줄('메움')과 못 옮긴 줄('못 옮김')을 엑셀에서 걸러 보게. 들여올 때는 넷째 칸까지만 읽는다.</summary>
    public const string Header = "결과 파일,번호,원문,번역,확인";   // loc:none

    public static string Write(IEnumerable<(string OutPath, IReadOnlyList<TextBlockInfo> Blocks)> pages)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append("\r\n");
        foreach (var (outPath, blocks) in pages)
            for (int i = 0; i < blocks.Count; i++)
                sb.Append(Quote(outPath)).Append(',').Append(i + 1).Append(',')
                  .Append(Quote(blocks[i].SourceText ?? "")).Append(',').Append(Quote(blocks[i].Translation ?? "")).Append(',').Append(Quote(CheckWord(blocks[i]))).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>'확인' 칸의 글 — 아직 번역기의 글 그대로인 확인할 줄만(<see cref="TextBlockInfo.NeedsReview"/>). 파일 안의 글이라 머리 줄처럼 한국어다.</summary>
    private static string CheckWord(TextBlockInfo b) =>
        !b.NeedsReview() ? "" : b.Review == ReviewMark.Filled ? "메움" : "못 옮김";   // loc:none

    /// <summary>칸 하나를 따옴표로 감싼다 — 안의 "는 ""로, 줄바꿈은 LF로(엑셀은 칸 안의 줄바꿈을 따옴표 안에서 받는다).</summary>
    private static string Quote(string field) => "\"" + field.Replace("\r\n", "\n").Replace("\"", "\"\"") + "\"";

    /// <summary>CSV 글 → 줄 목록. 머리 줄(번호 칸이 '번호')·빈 줄·번호가 숫자가 아니거나 칸이 넷이 안 되는 줄은 건너뛴다 — 머리 줄을 지운 표도 읽힌다(검수 2026-11-26). 칸 안의 CRLF는 LF로.</summary>
    public static List<Row> Parse(string csv)
    {
        var rows = new List<Row>();
        foreach (var fields in Records(csv ?? ""))
        {
            if (fields.Count < 4) continue;
            if (!int.TryParse(fields[1].Trim(), out int index)) continue;
            rows.Add(new Row(fields[0].Trim(), index, Lf(fields[2]), Lf(fields[3])));
        }
        return rows;
    }

    private static string Lf(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>레코드 단위로 칸을 나눈다 — 따옴표 안에서는 쉼표·줄바꿈이 칸을 가르지 않고 ""는 "다. 빈 레코드(빈 줄)는 내지 않는다. BOM은 뗀다.</summary>
    private static IEnumerable<List<string>> Records(string text)
    {
        var fields = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false, any = false;
        int i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cur.Append('"'); i++; }
                    else quoted = false;
                }
                else cur.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; any = true; break;
                case ',': fields.Add(cur.ToString()); cur.Clear(); any = true; break;
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n') break;   // CRLF — 다음 LF가 레코드를 끝낸다
                    goto case '\n';   // CR만 쓰는 옛 맥 꼴 — 레코드 끝(검수 2026-11-26)
                case '\n':
                    if (any || cur.Length > 0) { fields.Add(cur.ToString()); yield return fields; }
                    fields = new List<string>(); cur.Clear(); any = false;
                    break;
                default: cur.Append(c); any = true; break;
            }
        }
        if (any || cur.Length > 0) { fields.Add(cur.ToString()); yield return fields; }
    }

    /// <summary>표의 줄을 블록 목록에 적용한다 — 번호(1부터)로 찾되, 표의 원문과 블록의 원문이 다르면(둘 다 있을 때) 건너뛴다: 결과 창에서 블록을 넣고 뺀 뒤에는 번호가 밀려
    /// 엉뚱한 풍선을 덮었다(검수 2026-11-26). 번역 글만 바꾼다(빈 번역·같은 글·없는 번호는 무시, 앞뒤 빈칸은 떼고 CRLF는 LF로 — 견줄 때도 그렇게 맞춘다). 다른 칸(자리·스타일·효과음·숨김)은 그대로.
    /// 돌려주는 것은 새 목록, 바뀐 블록 수, 원문이 안 맞아 건너뛴 줄 수.</summary>
    public static (List<TextBlockInfo> Blocks, int Changed, int Mismatched) Apply(IReadOnlyList<TextBlockInfo> blocks, IEnumerable<Row> rows)
    {
        var list = blocks.ToList();
        int changed = 0, mismatched = 0;
        foreach (var r in rows)
        {
            int i = r.Index - 1;
            if (i < 0 || i >= list.Count) continue;
            var src = Lf(r.Source ?? "").Trim();
            var have = Lf(list[i].SourceText ?? "").Trim();
            if (src.Length > 0 && have.Length > 0 && src != have) { mismatched++; continue; }
            var tr = Lf(r.Translation ?? "").Trim();
            if (tr.Length == 0 || tr == Lf(list[i].Translation ?? "").Trim()) continue;
            list[i] = list[i] with { Translation = tr };
            changed++;
        }
        return (list, changed, mismatched);
    }
}
