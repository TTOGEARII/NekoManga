namespace NekoManga.Core.Tests;

/// <summary>테스트 하나가 쓰고 버리는 임시 폴더.
/// 테스트의 파일 입출력은 전부 이 안에서만 한다 — 사용자 데이터(%AppData%\NekoManga, 런타임 폴더, 원고 폴더)를 건드리지 않기 위해서다.</summary>
public sealed class TempDir : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("nekotest_").FullName;

    /// <summary>임시 폴더 안의 경로를 만든다 (하위 폴더는 만들지 않는다).</summary>
    public string At(params string[] parts) => Path.Combine([Root, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch { /* 잠긴 파일이 남아도 테스트 결과에는 영향이 없다 — OS 임시 폴더 정리에 맡긴다 */ }
    }
}
