using System.Text.Json;
using OpenCvSharp;

namespace NekoManga.Core.Pipeline;

/// <summary>검열 마스크 사이드카 (2026-10-21, 사용자 요청 "마스크 수정이 재실행에도 유지되게").
///
/// 결과 폴더의 <c>.neko\&lt;이름&gt;.mask.json / .mask.png / .auto.png / .pre.png</c>에 저장된다.
/// - mask.png: 지울 곳(255) — 원본 해상도 CV_8UC1. 사용자가 결과 창의 '마스크 수정' 탭에서 고치면 여기에 덮어쓴다.
/// - auto.png: 자동 감지 결과 사본 ('자동 감지로' 되돌리기용 + 학습 재료의 '자동' 쪽).
/// - pre.png: 검열 제거 직전 원고 (번역 글자만 지운 상태) — 마스크 탭이 보여 주는 그림이자 '채색부터 다시'의 입력.
/// 같은 원고를 다시 돌리면 러너가 감지 대신 이 마스크를 쓴다(ReuseStoredMasks).</summary>
public record MaskSidecar(
    string Source,          // 원본 파일 경로
    int SrcW, int SrcH,     // 원본 크기 (마스크 크기와 같다)
    string Summary,         // 감지 요약 (예: "AI(흰 0·검 4·모자이크 0 → 24705px)")
    bool Skip,              // 사용자가 '이 페이지는 검열 없음'으로 표시
    bool Edited,            // 사용자가 마스크를 손으로 고쳤는지
    bool Confirmed,         // 검열 제거를 진행한(또는 진행하라고 확인한) 마스크인지 — 재실행 때 검토를 다시 기다리지 않는다
    DateTime SavedAt)
{
    private static string Base(string outPath) =>
        Path.Combine(Path.GetDirectoryName(outPath)!, ".neko", Path.GetFileNameWithoutExtension(outPath));

    public static string JsonPathFor(string outPath) => Base(outPath) + ".mask.json";
    public static string MaskPathFor(string outPath) => Base(outPath) + ".mask.png";
    public static string AutoPathFor(string outPath) => Base(outPath) + ".auto.png";
    public static string PrePathFor(string outPath) => Base(outPath) + ".pre.png";

    /// <summary>json만 다시 쓴다 (마스크·원고 PNG는 그대로).</summary>
    public void Save(string outPath)
    {
        var path = JsonPathFor(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this));
    }

    /// <summary>마스크(+자동 감지 사본, 검열 제거 전 원고)를 PNG로 쓰고 json을 저장한다. 넘기지 않은 PNG는 있던 그대로 둔다.</summary>
    public static MaskSidecar Write(string outPath, string source, Mat mask, Mat? autoMask, Mat? pre,
        string summary, bool skip, bool edited, bool confirmed)
    {
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(outPath)!, ".neko"));
        ImgIo.Write(MaskPathFor(outPath), mask);
        if (autoMask != null) ImgIo.Write(AutoPathFor(outPath), autoMask);
        if (pre != null) ImgIo.Write(PrePathFor(outPath), pre);
        var sc = new MaskSidecar(source, mask.Width, mask.Height, summary, skip, edited, confirmed, DateTime.Now);
        sc.Save(outPath);
        return sc;
    }

    /// <summary>json·마스크 PNG가 모두 있을 때만 돌려준다.</summary>
    public static MaskSidecar? Load(string outPath)
    {
        try
        {
            var path = JsonPathFor(outPath);
            if (!File.Exists(path) || !File.Exists(MaskPathFor(outPath))) return null;
            return JsonSerializer.Deserialize<MaskSidecar>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    public static bool HasPre(string outPath) => File.Exists(PrePathFor(outPath));

    /// <summary>마스크 PNG → CV_8UC1 (255=지울 곳). 없거나 못 읽으면 null.</summary>
    public static Mat? LoadMask(string outPath) => LoadGray(MaskPathFor(outPath));
    public static Mat? LoadAuto(string outPath) => LoadGray(AutoPathFor(outPath));

    private static Mat? LoadGray(string path)
    {
        if (!File.Exists(path)) return null;
        var m = ImgIo.Read(path, ImreadModes.Grayscale);
        if (m.Empty()) { m.Dispose(); return null; }
        Cv2.Threshold(m, m, 127, 255, ThresholdTypes.Binary);
        return m;
    }
}
