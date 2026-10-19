using Microsoft.ML.OnnxRuntime;

namespace NekoManga.Core.Engine;

/// <summary>내장 엔진의 ONNX 세션 관리. DirectML(GPU) 우선, 실패 시 CPU 폴백.
/// 세션은 모델당 1개를 캐시해 재사용한다 (로드가 수 초 걸린다).</summary>
public sealed class OnnxEnv(AppSettings settings) : IDisposable
{
    private readonly Dictionary<string, InferenceSession> _sessions = new();
    private readonly object _lock = new();

    public string ModelsDir => Path.Combine(settings.RuntimeRoot, "models");

    public string ModelPath(string fileName) => Path.Combine(ModelsDir, fileName);

    public bool ModelExists(string fileName) => File.Exists(ModelPath(fileName));

    public InferenceSession Get(string fileName)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(fileName, out var s)) return s;
            var path = ModelPath(fileName);
            if (!File.Exists(path))
                throw new FileNotFoundException($"엔진 모델이 없어요: {fileName} — 설정 → 구성요소에서 설치해 주세요.", path);

            InferenceSession session;
            string ep;
            if (Environment.GetEnvironmentVariable("NEKO_FORCE_CPU") == "1")
            {
                session = new InferenceSession(path);
                ep = "CPU(forced)";
            }
            else
            {
                // DirectML 어댑터는 0번부터 차례로 시도한다. 0번이 가상 디스플레이 어댑터(Parsec 등)면 D3D12 장치 생성이
                // 887A0004(DEVICE_REMOVED)로 터져 **모든 모델이 조용히 CPU로 떨어진다** (2026-10-19 실측 — LaMa 1 ROI 3초로
                // 느려졌는데 결과는 같아서 알아채기 어려웠다). NEKO_DML_DEVICE=<n>으로 고정할 수 있다.
                session = null!; ep = "";
                var forced = Environment.GetEnvironmentVariable("NEKO_DML_DEVICE");
                var candidates = int.TryParse(forced, out var fd) && fd >= 0 ? new[] { fd } : new[] { 0, 1, 2, 3 };
                var errors = new List<string>();
                foreach (var dev in candidates)
                {
                    try
                    {
                        using var opt = new SessionOptions();   // ORT가 세션 생성 때 복사하므로 바로 해제해도 된다
                        opt.AppendExecutionProvider_DML(dev);
                        session = new InferenceSession(path, opt);
                        ep = $"DirectML(device {dev})";
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
            _sessions[fileName] = session;
            return session;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _sessions.Values) s.Dispose();
            _sessions.Clear();
        }
    }
}
