using NekoManga.Core.Comfy;
using NekoManga.Core.Decensor;
using NekoManga.Core.Engine;
using OpenCvSharp;
using System.Text.Json.Nodes;

namespace NekoManga.Core.Pipeline;

// 마스크 사이드카(.neko) — 저장·되읽기·확인 표시, 그리고 검토 결과를 학습 재료로 저장
public partial class PipelineRunner
{
    // ── 마스크 사이드카 (.neko) ──
    /// <summary>사이드카는 편의 기능이라 못 써도 파이프라인은 계속 가지만, 실패를 삼키면 "다시 돌려도 마스크가 유지된다"는
    /// 약속이 조용히 깨진다(디스크 가득·권한·OneDrive 잠금) — 행에 바로 알리고 완료 줄에도 남긴다.</summary>
    private bool SaveMaskSidecar(RunContext cx, ReviewPage pg, bool confirmed, bool edited, Mat? pre = null, bool freshDetection = false)
    {
        if (pg.Mask == null) return false;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (pre != null) MaskSidecar.Write(pg.OutPath, pg.File, pg.Mask, pg.AutoMask, pre, pg.Summary, pg.Skip, edited, confirmed, pg.Kinds, freshDetection);
                else
                {
                    using var img = ImgIo.Read(pg.WorkPath, ImreadModes.Color);
                    MaskSidecar.Write(pg.OutPath, pg.File, pg.Mask, pg.AutoMask, img.Empty() ? null : img, pg.Summary, pg.Skip, edited, confirmed);
                }
                if (MaskSidecar.HasPre(pg.OutPath)) return true;
                if (pre == null)
                {
                    // 검토 창 경로(원고를 다시 못 읽은 경우): json+mask는 저장됐고 검토도 이미 끝났다 — 결과 창 마스크 탭만 못 연다고 알리고 성공으로 둔다
                    Warn(cx, pg, "검열 제거 전 원고(.pre.png)를 쓰지 못해 결과 창 마스크 탭은 열리지 않아요");
                    return true;
                }
                // 페이지별 검토 경로: pre.png가 없으면 결과 창 마스크 탭이 열리지 않는다(검토 불가) — '저장됨'으로 치면 그 장이 풀 길 없는 대기에 걸린다
                pg.SidecarError = "검열 제거 전 원고(.pre.png)를 쓰지 못했어요";
                Warn(cx, pg, "마스크 저장 실패: " + pg.SidecarError);
                return false;
            }
            catch (IOException ex) when (attempt < 2 && (ex.HResult & 0xFFFF) is 32 or 33)   // 공유·잠금 위반만 기다릴 가치가 있다 — 디스크 가득·폴더 없음은 곧장 실패
            {
                Thread.Sleep(700);   // 결과 창 미리 읽기가 옛 .pre/.mask를 쥐고 있는 잠깐의 잠금 — 사이드카를 못 쓰면 그 장이 실패로 끝나니 한 번 더 기다려 본다
            }
            catch (Exception ex) { pg.SidecarError = ex.Message; Warn(cx, pg, "마스크 저장 실패: " + ex.Message); return false; }
        }
    }

    /// <summary>사이드카가 지울 곳을 약속하는데('검열 없음' 표시가 아닌데) 마스크 PNG를 그림으로 못 읽었다(0바이트·깨진 PNG) — 결과 창 [채색부터 다시]와 페이지별 검토의 확인이 같이 쓴다.</summary>
    private const string StoredMaskUnreadable = "저장된 마스크(.mask.png)를 읽지 못했어요 — 마스크 탭에서 다시 저장하거나 처음부터 다시 돌려 주세요.";

    /// <summary>사이드카 json이 있는데 깨져서(0바이트·쓰다 끊김) 못 읽었다 — 사용자가 무엇을 저장했는지('검열 없음' 표시·고친 마스크) 알 수 없다.
    /// 다시 돌리면 1차가 새로 감지해 사이드카를 다시 쓴다.</summary>
    private const string StoredSidecarUnreadable = "저장된 마스크 정보(.mask.json)를 읽지 못했어요 — 처음부터 다시 돌려 주세요.";

    /// <summary>사이드카 파일을 다른 프로그램이 끝내 쥐고 있어 못 읽었다 — 영어 OS 문구 대신 무엇을 못 읽었고 어떻게 하면 되는지.</summary>
    private static string StoredMaskLocked(string path) =>
        $"저장된 마스크(.neko 폴더의 {Path.GetFileName(path)})를 다른 프로그램이 쥐고 있어(백신 검사·동기화 등) 읽지 못했어요 — 잠시 뒤 다시 돌려 주세요.";

    /// <summary>결과 창이 사이드카에 저장한 마스크·건너뜀을 페이지에 되읽는다 (크기가 같을 때만).
    /// 마스크 PNG가 있는데 그림으로 못 읽으면 던진다 — 메모리의 자동 감지 마스크로 그냥 지우면 사용자가 지운 오탐 자리까지 칠하고 '확인됨'이 찍힌다.
    /// json이 있는데 못 읽어도(잠김·깨짐) 던진다 — '사이드카 없음'으로 넘기면 같은 일이 난다. 사이드카가 정말 없을 때만 메모리의 마스크로 간다.</summary>
    private static void ReloadMaskFromSidecar(ReviewPage pg)
    {
        var sc = LoadSidecarWaitingOutLock(pg.OutPath, brokenThrows: true);
        if (sc == null || !sc.MatchesSource(pg.File)) return;   // 그 자리 사이드카가 다른 원고 것이면 받지 않는다
        pg.Skip = sc.Skip;
        using var m = LoadMaskWaitingOutLock(pg.OutPath);
        // 파일 자체가 없으면(그사이 지워짐) 전처럼 메모리의 마스크로 간다
        if (m == null && !sc.Skip && File.Exists(MaskSidecar.MaskPathFor(pg.OutPath))) throw new InvalidDataException(StoredMaskUnreadable);
        if (m != null && pg.Mask != null && m.Size() == pg.Mask.Size()) m.CopyTo(pg.Mask);
    }

    /// <summary>마스크 PNG를 읽되 공유·잠금 위반이면 두 번까지 기다렸다 다시 읽는다(<see cref="SaveMaskSidecar"/>와 같은 요령) — 결과 창이 막 저장한 마스크를
    /// 백신·동기화가 잠깐 쥐고 있으면 확인한 장이 그 때문에 실패한다. 그래도 잠겨 있으면 던진다(<see cref="StoredMaskLocked"/>).
    /// 페이지별 검토의 확인, 다시 돌릴 때의 저장 마스크 재사용, 결과 창 [채색부터 다시]가 같이 쓴다.</summary>
    private static Mat? LoadMaskWaitingOutLock(string outPath)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return MaskSidecar.LoadMask(outPath); }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
            {
                if (attempt >= 2) throw new IOException(StoredMaskLocked(MaskSidecar.MaskPathFor(outPath)), ex);
                Thread.Sleep(700);
            }
        }
    }

    /// <summary>마스크 사이드카(json)를 읽되 '있는데 못 읽음'을 '사이드카 없음'과 가른다 — <see cref="MaskSidecar.Load"/>는 잠김·깨짐도 null로 삼켜,
    /// 결과 창이 막 저장한 json을 백신·동기화가 잠깐 쥐고 있으면 사용자가 고친 마스크·'검열 없음' 표시를 없는 것으로 치고 자동 감지 마스크로 지우러 갔다.
    /// 잠겨 있으면 마스크 PNG처럼 두 번까지 기다렸다 다시 읽고, 그래도 잠겨 있으면 던진다. 깨진 json은 <paramref name="brokenThrows"/>면 던지고,
    /// 아니면 null이다(다시 돌릴 때의 1차는 그때 새로 감지한다). json이나 마스크 PNG가 정말 없으면 null.</summary>
    internal static MaskSidecar? LoadSidecarWaitingOutLock(string outPath, bool brokenThrows)
    {
        var json = MaskSidecar.JsonPathFor(outPath);
        for (int attempt = 0; ; attempt++)
        {
            if (MaskSidecar.Load(outPath) is { } sc) return sc;
            if (!File.Exists(json) || !File.Exists(MaskSidecar.MaskPathFor(outPath))) return null;
            if (!IsLocked(json))
            {
                // 잠겨 있지 않은데 못 읽었다 — 읽는 사이에 풀렸을 수 있어 한 번 더 읽어 보고, 그래도 안 읽히면 깨진 json이다
                if (MaskSidecar.Load(outPath) is { } unlocked) return unlocked;
                if (!IsLocked(json))
                {
                    if (brokenThrows) throw new InvalidDataException(StoredSidecarUnreadable);
                    return null;
                }
            }
            if (attempt >= 2) throw new IOException(StoredMaskLocked(json));
            Thread.Sleep(700);
        }
    }

    /// <summary>사이드카에 '확인됨'을 표시한다. 실패하면 경고로 알리고 false.</summary>
    private static bool MarkSidecarConfirmed(RunContext cx, ReviewPage pg)
    {
        try { if (MaskSidecar.Load(pg.OutPath) is { } sc && !sc.Confirmed) (sc with { Confirmed = true, Skip = pg.Skip }).Save(pg.OutPath); return true; }
        catch (Exception ex) { Warn(cx, pg, "마스크 확인 표시 실패: " + ex.Message); return false; }
    }

    private static bool MasksEqual(Mat a, Mat b)
    {
        if (a.Size() != b.Size()) return false;
        using var diff = new Mat();
        Cv2.BitwiseXor(a, b, diff);
        return Cv2.CountNonZero(diff) == 0;
    }

    /// <summary>검토 창의 결과를 학습 재료로 저장한다 — 페이지마다 (원고 png, 자동 감지 마스크, 최종 마스크) 세 파일 + index.jsonl 한 줄.
    /// 자동 감지와 최종이 같고 둘 다 비었으면(검열 없는 페이지를 그냥 지나침) 깨끗 페이지 재료로도 쓸 수 있어 함께 저장한다.
    /// 경로: &lt;DataDir&gt;\training\corrections\&lt;yyyyMMdd&gt;\&lt;원본이름&gt;_&lt;HHmmss&gt;.{page,auto,mask}.png</summary>
    public static string CorrectionsDir => Path.Combine(AppSettings.DataDir, "training", "corrections");

    private void SaveCorrections(IReadOnlyList<ReviewPage> pages)
    {
        var dir = Path.Combine(CorrectionsDir, DateTime.Now.ToString("yyyyMMdd"));
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("HHmmss");
        var lines = new List<string>();
        foreach (var pg in pages)
        {
            if (pg.Mask == null) continue;
            var stem = pg.WorkKey + "_" + stamp;   // 한 번에 여러 장을 같은 시각으로 저장한다 — 다른 폴더의 같은 이름 원고가 서로 덮지 않게 순번 포함
            using var img = ImgIo.Read(pg.WorkPath, ImreadModes.Color);
            if (img.Empty()) continue;
            ImgIo.Write(Path.Combine(dir, stem + ".page.png"), img);
            ImgIo.Write(Path.Combine(dir, stem + ".mask.png"), pg.Mask);
            if (pg.AutoMask != null) ImgIo.Write(Path.Combine(dir, stem + ".auto.png"), pg.AutoMask);
            int autoPx = pg.AutoMask == null ? 0 : Cv2.CountNonZero(pg.AutoMask);
            int finalPx = Cv2.CountNonZero(pg.Mask);
            int changed = 0;
            if (pg.AutoMask != null) { using var diff = new Mat(); Cv2.BitwiseXor(pg.AutoMask, pg.Mask, diff); changed = Cv2.CountNonZero(diff); }
            lines.Add($"{{\"stem\":\"{stem}\",\"source\":{System.Text.Json.JsonSerializer.Serialize(pg.File)},\"width\":{img.Width},\"height\":{img.Height}," +
                      $"\"auto_px\":{autoPx},\"final_px\":{finalPx},\"changed_px\":{changed},\"skip\":{(pg.Skip ? "true" : "false")}," +
                      $"\"summary\":{System.Text.Json.JsonSerializer.Serialize(pg.Summary)},\"model\":\"{Engine.CensorNet.ModelFile}\"}}");
        }
        if (lines.Count > 0) File.AppendAllLines(Path.Combine(dir, "index.jsonl"), lines);
    }
}
