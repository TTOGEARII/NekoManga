namespace NekoManga.Core.Tests;

/// <summary>환경변수·현재 폴더처럼 프로세스 전체가 같이 쓰는 상태를 바꾸는 테스트 묶음 — 다른 테스트와 동시에 돌지 않는다.
/// NEKO_SETTINGS·NEKO_LAMA_MODEL·NEKO_FORCE_CPU 같은 환경변수를 바꾸는 테스트 클래스에는 [Collection(EnvCollection.Name)]을 붙인다.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvCollection
{
    public const string Name = "Env";
}
