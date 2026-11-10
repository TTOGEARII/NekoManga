using NekoManga.Core.Comfy;

namespace NekoManga.Core.Tests;

/// <summary>ComfyServer — 서버를 띄우거나 부르지 않고 확인할 수 있는 주소 규칙만.</summary>
public class ComfyServerTests
{
    [Theory]
    [InlineData(8188)]
    [InlineData(8190)]
    public async Task BaseUrl_UsesLoopbackAddressAndConfiguredPort(int port)
    {
        await using var server = new ComfyServer(new AppSettings { ComfyPort = port });

        // 앱이 띄우는 엔진은 이 PC의 127.0.0.1에서 듣는다 — 다른 프로그램이 8188을 쓰면 사용자가 바꾼 포트를 따라가야 한다
        Assert.Equal($"http://127.0.0.1:{port}", server.BaseUrl);
        Assert.False(server.WeStartedIt);
    }
}
