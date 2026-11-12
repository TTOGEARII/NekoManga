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

    public string ModelPath(string fileName) => Path.Combine(ModelsDir, fileName);

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
    /// "Auto"(기본): **전용 VRAM이 6GB 이상이고 ComfyUI를 쓰지 않는 작업일 때만 GPU.** ComfyUI를 쓰는 작업(자동 채색·재생성·커스텀)이면 CPU —
    /// 내장 모델을 전부 GPU에 올리면 VRAM을 ~5GB 잡아(실측) 8GB 카드에서 SDXL과 다투다 메모리 부족이 난다.
    /// "On" = 항상 GPU(VRAM 검사 없음), "Off" = 항상 CPU. NEKO_FORCE_CPU=1이 가장 우선.</summary>
    public bool GpuAllowed
    {
        get
        {
            if (Environment.GetEnvironmentVariable("NEKO_FORCE_CPU") == "1") return false;
            return settings.OnnxGpu switch
            {
                "On" => true,
                "Off" => false,
                _ => BestVramMb.Value >= AutoMinVramMb
                     && !(settings.EnableAutoColor || (settings.EnableDecensor && settings.DecensorMode is "ComfyRegen" or "ComfyCustom")),
            };
        }
    }

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
                    Console.Error.WriteLine($"[onnx] GPU 실행 실패 — CPU로 다시 돌려요 ({string.Join(", ", files)}): {ex.Message.Split('\n')[0]}");
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
            if (_sessions.TryGetValue(Key(fileName, cpuOnly), out var s)) return s;
            var path = ModelPath(fileName);
            if (!File.Exists(path))
                throw new FileNotFoundException($"엔진 모델이 없어요: {fileName} — 설정 → 구성요소에서 설치해 주세요.", path);

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
                ep = _demoted.Contains(fileName) ? "CPU(GPU 실행 실패 뒤)" : "CPU(설정 — ComfyUI와 VRAM을 다투지 않게)";
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
