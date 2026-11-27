using Microsoft.ML.OnnxRuntime;

namespace NekoManga.Core.Engine;

/// <summary>내장 엔진의 ONNX 세션 관리. DirectML(GPU) 우선, 실패 시 CPU 폴백.
/// 세션은 모델당 1개를 캐시해 재사용한다 (로드가 수 초 걸린다).
///
/// **2026-11-09까지 DirectML은 한 번도 켜진 적이 없었다** — 실행 파일 옆에 DirectML.dll이 없어(NuGet 패키지가 x64 지정 프로젝트에만 복사)
/// Windows에 딸린 옛 DirectML 1.0이 대신 로드됐고, 세션 생성이 887A0004(DXGI_ERROR_UNSUPPORTED — '장치 제거'가 아니다)로 실패해
/// 모든 모델이 조용히 CPU로 돌았다. App.csproj가 DLL을 출력에 넣도록 고친 뒤로는 이 클래스가 실제로 GPU 세션을 만든다.</summary>
public sealed class OnnxEnv(AppSettings settings) : IDisposable
{
    private readonly Dictionary<string, InferenceSession> _sessions = new();
    private readonly HashSet<string> _gpuKeys = new();
    private readonly object _lock = new();

    public string ModelsDir => Path.Combine(settings.RuntimeRoot, "models");

    public string ModelPath(string fileName) => Path.Combine(ModelsDir, PlainName(fileName));

    // ── 그래프 융합을 끈 세션 ──
    // DirectML은 모델 전체를 한 덩어리(DmlFusedNode)로 묶어 돌린다(그래프 융합). 그 덩어리가 글자 감지기(ctd)와 LaMa에서 GPU를 멈추게 한다 —
    // 실측(2026-11-27, RTX 2070 SUPER, 드라이버 32.0.15.9186, DirectML 1.15.4, 모델 하나만 올려 임의 입력으로): 첫 실행 2.2~2.5초(결과도 CPU와 어긋난다) 뒤
    // 둘째 실행이 887A0006(DXGI 장치 멈춤)으로 죽고, 그 뒤로는 같은 프로세스의 다른 모델도 그 장치에 세션을 못 만든다(전부 CPU로 내려갔다 — 디스플레이 드라이버가 재시작되기도 했다).
    // 융합을 끄면(세션 설정 ep.dml.disable_graph_fusion=1 — 노드마다 따로 돈다) 감지기 55ms(CPU 860ms) · LaMa 107ms(CPU 1,780ms)로 멀쩡히 돈다. 글자 읽기(manga-ocr)는 융합한 채로도 돈다(20ms).
    // ComfyUI가 쥔 메모리·메타커맨드·메모리 아레나·어댑터 번호와는 상관없었다(하나씩 바꿔 봤다).
    // 융합을 끈 세션은 **같은 파일의 다른 이름**(<see cref="Unfused"/>)으로 따로 둔다: 이름을 안 바꾼 호출(검열 제거가 쓰는 세션들)은 전과 똑같이 돈다.
    private const string UnfusedMark = "?unfused";
    /// <summary>그 모델을 GPU에서 그래프 융합 없이 돌릴 세션의 이름 — <see cref="Get"/>·<see cref="Guard{T}"/>·<see cref="UsesGpu"/>·<see cref="Demote"/>에 파일 이름 대신 넘긴다.</summary>
    internal static string Unfused(string fileName) => IsUnfused(fileName) ? fileName : fileName + UnfusedMark;
    internal static bool IsUnfused(string name) => name.EndsWith(UnfusedMark, StringComparison.Ordinal);
    internal static string PlainName(string name) => IsUnfused(name) ? name[..^UnfusedMark.Length] : name;
    /// <summary>DirectML 세션을 만들 때 걸 세션 설정 — 융합을 끈 이름이면 융합 끄기 하나.</summary>
    internal static IReadOnlyDictionary<string, string> DmlSessionConfig(string name) =>
        IsUnfused(name) ? new Dictionary<string, string> { ["ep.dml.disable_graph_fusion"] = "1" } : new Dictionary<string, string>();
    /// <summary>융합을 끈 이름으로 온 요청이 실제로 쓸 세션: GPU를 쓰지 않는 실행(설정·강제 CPU·CPU 전용 요청)이면 원래 이름의 세션을 같이 쓴다 — CPU에서는 융합이 뜻이 없고,
    /// 같은 모델을 이름만 달리해 두 번 올리지 않는다. GPU에서 실패해 내려간 뒤(<paramref name="demoted"/>)에는 원래 이름의 **CPU 전용** 세션으로 —
    /// 원래 이름의 보통 세션은 융합한 GPU 세션일 수 있다(그리로 가면 안 된다). 융합을 끄지 않은 이름은 그대로 돌려준다.</summary>
    internal static (string Name, bool CpuOnly) ResolveSession(string name, bool cpuOnly, bool gpuWanted, bool demoted)
    {
        if (!IsUnfused(name)) return (name, cpuOnly);
        if (cpuOnly || !gpuWanted) return (PlainName(name), cpuOnly);
        return demoted ? (PlainName(name), true) : (name, false);
    }

    public bool ModelExists(string fileName) => File.Exists(ModelPath(fileName));

    private static string Key(string fileName, bool cpuOnly) => cpuOnly ? fileName + "|cpu" : fileName;

    private readonly HashSet<string> _demoted = new();   // GPU에서 실행 오류가 난 모델 — 이 환경에서는 CPU로만
    private readonly List<InferenceSession> _retired = new();   // 내려진 GPU 세션 — 다른 스레드가 아직 쓰고 있을 수 있어 Dispose 때까지 닫지 않는다
    private readonly object _gpuRun = new();

    /// <summary>GPU 세션 실행을 한 줄로 세우는 잠금. DirectML 세션은 여러 스레드가 동시에 Run하는 것을 보장하지 않는다 —
    /// 페이지별 검토 흐름에서는 1차(글자 지움)와 2차(검열 제거)가 같은 LaMa 세션을 동시에 쓸 수 있다.</summary>
    public object GpuSync => _gpuRun;

    /// <summary>'자동'이 GPU를 쓰는 최소 전용 VRAM(MB). 내장 모델을 전부 올리면 ~5GB(실측)라 그보다 작은 카드·내장 그래픽은 공유 메모리로 밀려 CPU보다 느려질 수 있다.</summary>
    public const int AutoMinVramMb = 6000;
    private static readonly Lazy<int> BestVramMb = new(Runtime.SystemCheck.BestAdapterVramMb);

    /// <summary>지금 설정에서 내장 모델을 GPU(DirectML)에 올려도 되는가 (AppSettings.OnnxGpu).
    /// "Auto"(기본): **전용 VRAM이 6GB 이상이고 ComfyUI를 쓰지 않는 작업일 때만 GPU.** ComfyUI를 쓰는 작업(자동 채색·재생성·커스텀·글자 자리 다시 그리기 — 러너가 <see cref="JobUsesComfy"/>로 알려 준다)이면 CPU —
    /// 내장 모델을 전부 GPU에 올리면 VRAM을 ~5GB 잡아(실측) 8GB 카드에서 SDXL과 다투다 메모리 부족이 난다.
    /// "On" = 항상 GPU(VRAM 검사 없음), "Off" = 항상 CPU. NEKO_FORCE_CPU=1이 가장 우선.</summary>
    public bool GpuAllowed =>
        Allowed(settings.OnnxGpu, Environment.GetEnvironmentVariable("NEKO_FORCE_CPU") == "1", VramMb, JobUsesComfy ?? settings.UsesComfyApartFromTextRegen);

    /// <summary>이번 실행이 ComfyUI를 쓰는가 — 러너가 시작할 때(모델을 올리기 전에) 정해 준다(<c>PipelineRunner.PrepareAsync</c>). 설정만 보면 '글자 자리 다시 그리기'를 켜 둔 사람은
    /// 결과 창의 다시 지우기·다시 식자(SDXL을 부르지 않는 실행)까지 내장 AI가 CPU로 돈다(검토 2026-11-26).
    /// null이면(러너의 준비를 거치지 않는 길 — 결과 창의 제자리 지우기 <c>ManualDecensorAsync</c>·부위 감지·진단 진입점) 설정으로 보되 **글자 자리 다시 그리기는 세지 않는다**:
    /// 그 기능은 번역기를 쓰는 실행에서만 돌고 거기서는 늘 이 값이 정해진다(검토 2026-11-27 — 세면 그런 길까지 CPU로 돈다).</summary>
    public bool? JobUsesComfy { get; set; }

    /// <summary>전용 VRAM(MB)을 읽는 길 — 시험이 바꿔 끼운다(기본은 레지스트리에서 한 번 읽은 값).</summary>
    internal Func<int> VramMb { get; init; } = () => BestVramMb.Value;

    /// <summary><see cref="GpuAllowed"/>의 판정만 떼어 낸 것(시험이 표로 본다). VRAM은 '자동'일 때만 묻는다.</summary>
    internal static bool Allowed(string onnxGpu, bool forceCpu, Func<int> vramMb, bool usesComfy) => !forceCpu && onnxGpu switch
    {
        "On" => true,
        "Off" => false,
        _ => !usesComfy && vramMb() >= AutoMinVramMb,   // 글자 자리 다시 그리기(TextRegen)도 SDXL을 쓴다(2026-11-26)
    };

    /// <summary>모델 실행을 감싼다: GPU 세션이면 한 번에 하나씩만 돌리고(<see cref="GpuSync"/>), 실행 오류(VRAM 부족·드라이버 재시작 등)가 나면
    /// 그 모델을 CPU로 내리고 한 번 더 돌린다. <paramref name="body"/>는 다시 불려도 되는 것이어야 한다(안에서 <see cref="Get"/>으로 세션을 새로 받는다).
    /// 내려진 GPU 세션은 바로 닫지 않는다 — 잠금 밖에서(예: 감지기) 아직 그 세션을 쓰는 스레드가 있을 수 있다.</summary>
    public T Guard<T>(Func<T> body, params string[] files)
    {
        foreach (var f in files) Get(f);   // 세션을 먼저 만들어 둬야 GPU 여부를 안다
        bool gpu;
        lock (_lock) gpu = files.Any(_gpuKeys.Contains);
        if (!gpu) return body();
        lock (_gpuRun)
        {
            try { return body(); }
            catch (OnnxRuntimeException ex)
            {
                lock (_lock)
                {
                    foreach (var f in files.Where(_gpuKeys.Contains).ToArray())
                    {
                        _demoted.Add(f);
                        _gpuKeys.Remove(f);
                        if (_sessions.Remove(f, out var s) && !_sessions.ContainsValue(s)) _retired.Add(s);
                    }
                }
                if (Environment.GetEnvironmentVariable("NEKO_ONNX_DBG") == "1")
                    Console.Error.WriteLine($"[onnx] GPU 실행 실패 — CPU로 다시 돌려요 ({string.Join(", ", files)}): {ex.Message.Split('\n')[0]}");   // loc:none
                return body();   // Get이 _demoted를 보고 CPU 세션을 준다. 먼저 내려간 뒤에 들어온 스레드도 같은 길로 CPU에서 돈다
            }
        }
    }

    /// <param name="cpuOnly">true면 DirectML을 시도하지 않고 CPU 세션으로 연다 — GPU에서 수치가 깨지는 것으로 알려진 모델용
    /// (Aletheia Lens의 DeepCreamPy bar/mosaic: 원본도 CUDA에서 non-finite가 나와 CPU에 고정한다). 같은 파일의 GPU 세션과 따로 캐시된다.</param>
    public InferenceSession Get(string fileName, bool cpuOnly = false)
    {
        lock (_lock)
        {
            if (IsUnfused(fileName))
                (fileName, cpuOnly) = ResolveSession(fileName, cpuOnly, gpuWanted: Environment.GetEnvironmentVariable("NEKO_FORCE_CPU") != "1" && GpuAllowed, demoted: _demoted.Contains(fileName));
            if (_sessions.TryGetValue(Key(fileName, cpuOnly), out var s)) return s;
            var path = ModelPath(fileName);
            if (!File.Exists(path))
                throw new FileNotFoundException(Loc.F($"엔진 모델이 없다냥: {fileName} — 설정 → 구성요소에서 설치해 줘냥."), path);

            InferenceSession session;
            string ep;
            bool gpu = false;
            if (cpuOnly)
            {
                session = new InferenceSession(path);
                ep = "CPU(pinned)";
            }
            else if (Environment.GetEnvironmentVariable("NEKO_FORCE_CPU") == "1")
            {
                session = new InferenceSession(path);
                ep = "CPU(forced)";
            }
            else if (!GpuAllowed || _demoted.Contains(fileName))
            {
                session = new InferenceSession(path);
                ep = _demoted.Contains(fileName) ? "CPU(GPU 실행 실패 뒤)" : "CPU(설정 — ComfyUI와 VRAM을 다투지 않게)";   // loc:none
            }
            else
            {
                // DirectML 어댑터는 0번부터 차례로 시도한다 (0번이 가상 디스플레이 어댑터일 수 있다). NEKO_DML_DEVICE=<n>으로 고정할 수 있다.
                session = null!; ep = "";
                var forced = Environment.GetEnvironmentVariable("NEKO_DML_DEVICE");
                bool pinned = int.TryParse(forced, out var fd) && fd >= 0;
                var candidates = pinned ? new[] { fd } : new[] { 0, 1, 2, 3 };
                var errors = new List<string>();
                if (!pinned)
                {
                    // 먼저 '고성능 GPU'를 고르게 한다 — 번호 순서(0번)는 노트북에서 보통 내장 그래픽이다. 이 옵션을 모르는 환경이면 아래 번호 순서로.
                    try
                    {
                        using var opt = new SessionOptions();
                        foreach (var (key, value) in DmlSessionConfig(fileName)) opt.AddSessionConfigEntry(key, value);
                        opt.AppendExecutionProvider("DML", new Dictionary<string, string> { ["performance_preference"] = "high_performance", ["device_filter"] = "gpu" });
                        session = new InferenceSession(path, opt);
                        ep = "DirectML(high performance)";
                        gpu = true;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"hp: {ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()}");
                    }
                }
                if (session == null) foreach (var dev in candidates)
                {
                    try
                    {
                        using var opt = new SessionOptions();   // ORT가 세션 생성 때 복사하므로 바로 해제해도 된다
                        foreach (var (key, value) in DmlSessionConfig(fileName)) opt.AddSessionConfigEntry(key, value);
                        opt.AppendExecutionProvider_DML(dev);
                        session = new InferenceSession(path, opt);
                        ep = $"DirectML(device {dev})";
                        gpu = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"dev{dev}: {ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()}");
                    }
                }
                if (session == null)
                {
                    // DirectML 불가 환경(원격 세션·구형 GPU 등) — CPU로 폴백
                    session = new InferenceSession(path);
                    ep = "CPU(fallback: " + string.Join(" | ", errors) + ")";
                }
            }
            // 진단: NEKO_ONNX_DBG=1 이면 모델별 실행 공급자를 stderr에 적는다 (조용한 CPU 폴백을 잡는 용도)
            if (Environment.GetEnvironmentVariable("NEKO_ONNX_DBG") == "1")
                Console.Error.WriteLine($"[onnx] {fileName}: {ep}");
            _sessions[Key(fileName, cpuOnly)] = session;
            if (gpu) _gpuKeys.Add(Key(fileName, cpuOnly));
            else _sessions.TryAdd(Key(fileName, true), session);   // GPU를 못 쓴 세션은 곧 CPU 세션이다 — CPU용으로 하나 더 열지 않게 같이 걸어 둔다
            return session;
        }
    }

    /// <summary>그 모델의 (cpuOnly가 아닌) 세션이 실제로 GPU(DirectML)에서 도는가 — 세션이 없으면 만든다.</summary>
    public bool UsesGpu(string fileName)
    {
        Get(fileName);
        lock (_lock) return _gpuKeys.Contains(fileName);
    }

    /// <summary>그 모델을 이 실행 동안 CPU로 내린다 — GPU가 오류 없이 틀린 결과를 낼 때(호출부가 CPU 결과와 대조해 안다). 다음 <see cref="Get"/>부터 CPU 세션을 준다.
    /// 내려진 GPU 세션은 Guard와 같이 바로 닫지 않는다(아직 쓰는 스레드가 있을 수 있다).</summary>
    public void Demote(string fileName)
    {
        lock (_lock)
        {
            _demoted.Add(fileName);
            _gpuKeys.Remove(fileName);
            if (_sessions.Remove(fileName, out var s) && !_sessions.ContainsValue(s)) _retired.Add(s);
        }
    }

    /// <summary>캐시된 세션 하나를 닫는다 (한 번 쓰고 말 검증용 CPU 세션 등). 같은 세션이 다른 키로도 걸려 있으면 그대로 둔다.</summary>
    public void Release(string fileName, bool cpuOnly)
    {
        lock (_lock)
        {
            string key = Key(fileName, cpuOnly);
            if (!_sessions.Remove(key, out var s)) return;
            _gpuKeys.Remove(key);
            if (!_sessions.ContainsValue(s)) s.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _sessions.Values.Concat(_retired).Distinct()) s.Dispose();
            _sessions.Clear();
            _retired.Clear();
            _gpuKeys.Clear();
        }
    }
}
