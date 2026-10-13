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
            if (Environment.GetEnvironmentVariable("NEKO_FORCE_CPU") == "1")
            {
                session = new InferenceSession(path);
            }
            else
            {
                try
                {
                    var opt = new SessionOptions();
                    opt.AppendExecutionProvider_DML(0);
                    session = new InferenceSession(path, opt);
                }
                catch
                {
                    // DirectML 불가 환경(원격 세션·구형 GPU 등) — CPU로 폴백
                    session = new InferenceSession(path);
                }
            }
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
