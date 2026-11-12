namespace NekoManga.App;

/// <summary>검열 감지·복원 조합을 사람이 읽는 한 줄로 풀어 주는 문구 모음 (2026-11-09) — 설정 창의 '지금 설정' 요약, 메인 창 카드가 같이 쓴다.
/// 감지·복원이 따로 골라져서, 고른 값만 봐서는 실제로 무엇이 도는지 알기 어렵다는 피드백으로 만들었다.
/// ('이전 방식' 엔진 분기는 2026-11-12 사용자 선택으로 삭제 — 감지는 늘 Aletheia Lens + 전용 AI 보완.)
/// 설정 창은 아직 저장 전인 콤보 값을 넘기므로 AppSettings가 아니라 값들을 받는다.</summary>
internal static class DecensorLabels
{
    /// <summary>보완(기존 방식)으로 찾는 자리가 생길 수 있는 조합인가 — 자동(보완 켬)·흰 막대·색 검열.</summary>
    public static bool HasAssist(string target, string assist) =>
        target is "WhiteBar" or "Color" || (target == "Auto" && assist != "None");

    /// <summary>① 찾기 — 누가 무엇을 찾는지.</summary>
    public static string Find(string target, string assist, bool censorNetInstalled) => target switch
    {
        "Auto" => "Aletheia Lens가 막대·모자이크를 찾" + (assist switch
        {
            "None" => "아요.",
            _ when !censorNetInstalled => "아요. (전용 AI가 설치돼 있지 않아 보완은 쉬어요)",
            "WhiteMosaic" => "고, 전용 AI가 놓친 흰 막대선·모자이크를 보완해요.",
            "All" => "고, 전용 AI가 찾은 것(흰·검·모자이크)도 전부 합쳐요.",
            _ => "고, 전용 AI가 놓친 가는 흰 막대선을 보완해요.",
        }),
        "BlackBar" => "Aletheia Lens가 찾은 막대 중 검은 것만 써요.",
        "WhiteBar" => censorNetInstalled ? "Aletheia Lens가 찾은 막대 중 흰 것에, 전용 AI가 찾은 가는 흰 막대선을 더해요."
            : "Aletheia Lens가 찾은 막대 중 흰 것에, 흰 막대 규칙 감지를 더해요. (전용 AI를 설치하면 잘못 잡는 곳이 줄어요)",
        "Mosaic" => "Aletheia Lens가 찾은 모자이크만 써요.",
        _ => "Aletheia Lens가 찾은 막대 중 색 있는 것에, 색 검열 규칙 감지를 더해요.",
    };

    /// <summary>② 지우기 — 무엇으로 복원하는지. GPU 방식을 골랐으면 Aletheia 복원이 안 쓰인다는 점을 덧붙인다.</summary>
    public static string Fix(string mode, string target, string assist, string assistRepair)
    {
        const string tail = " Aletheia 복원을 쓰려면 '기본'을 고르세요.";
        return mode switch
        {
            "ComfyRegen" => "SDXL 재생성(ComfyUI·GPU)으로 다시 그려요." + tail,
            "ComfyCustom" => "내 ComfyUI 워크플로(GPU)로 지워요." + tail,
            _ => "Aletheia Lens 복원 — 막대는 막대 모델, 모자이크는 모자이크 모델로 지워요." +
                 (HasAssist(target, assist)
                     ? (assistRepair == "Aletheia" ? " 보완으로 찾은 자리도 Aletheia 막대 모델로 지워요." : " 보완으로 찾은 자리만 LaMa로 지워요.")
                     : ""),
        };
    }

    /// <summary>메인 창 검열 제거 카드의 한 줄.</summary>
    public static string Card(NekoManga.Core.AppSettings s) => s.DecensorMode switch
    {
        "ComfyRegen" => "Aletheia AI가 검열을 찾아 SDXL로 재생성 (GPU·내장 레시피)",
        "ComfyCustom" => "Aletheia AI가 검열을 찾아 커스텀 ComfyUI 워크플로로 복원 (GPU)",
        _ => "Aletheia AI가 검열을 찾아 Aletheia로 복원 (기본)",
    };
}
