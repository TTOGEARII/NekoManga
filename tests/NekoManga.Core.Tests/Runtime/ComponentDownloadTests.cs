using System.Net;
using System.Net.Http.Headers;
using NekoManga.Core.Runtime;

namespace NekoManga.Core.Tests;

/// <summary>ComponentInstaller의 내려받기 — 네트워크 없이 가짜 응답(Send 이음새)으로 끊김·멈춤·이어받기·잘못된 응답을 본다.</summary>
public class ComponentDownloadTests
{
    private static readonly byte[] Body = [.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 7))];

    private static Component FileComponent(string? sha = null, bool civitai = false) => new(
        "test-file", "시험 파일", ComponentKind.File, "https://example.invalid/file.bin", "models/file.bin", "models/file.bin", Body.Length,
        UseCivitaiKey: civitai, Sha256: sha);

    private static ComponentInstaller Make(TempDir t, Func<HttpRequestMessage, int, HttpResponseMessage> reply, List<long?> ranges)
    {
        int n = 0;
        return new ComponentInstaller(new AppSettings { RuntimeRoot = t.At("rt") })
        {
            IdleTimeout = TimeSpan.FromMilliseconds(300),
            RetryDelay = TimeSpan.Zero,
            Send = (req, _) =>
            {
                ranges.Add(req.Headers.Range?.Ranges.Single().From);
                return Task.FromResult(reply(req, n++));
            },
        };
    }

    /// <summary>from부터의 본문을 주는 응답 — cut 바이트를 준 뒤 끊기거나(IOException) stall이면 멈춘다(읽기가 끝나지 않음).</summary>
    private static HttpResponseMessage Respond(long from, int? cut = null, bool stall = false)
    {
        var rest = Body[(int)from..];
        var resp = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        {
            Content = new StreamContent(new FlakyStream(rest, cut, stall)),
        };
        resp.Content.Headers.ContentLength = rest.Length;
        resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return resp;
    }

    private sealed class FlakyStream(byte[] data, int? cut, bool stall) : Stream
    {
        private int _pos;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int limit = cut ?? data.Length;
            if (_pos >= limit)
            {
                if (cut == null) return 0;
                if (!stall) throw new IOException("연결이 끊겼다");
                await Task.Delay(Timeout.Infinite, ct);   // 멈춘 연결 — 취소될 때까지 돌아오지 않는다
            }
            int n = Math.Min(Math.Min(buffer.Length, 1000), limit - _pos);
            data.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(false)]   // 받는 도중 연결이 끊김
    [InlineData(true)]    // 받는 도중 응답이 멈춤(와이파이가 끊겨 읽기가 돌아오지 않음) — 예전에는 앱을 끌 때까지 그대로 멈춰 있었다
    public async Task Download_ConnectionDropsOrStalls_ResumesFromWhereItStopped(bool stall)
    {
        using var t = new TempDir();
        var ranges = new List<long?>();
        var installer = Make(t, (req, n) => n == 0 ? Respond(0, cut: 2000, stall) : Respond(req.Headers.Range!.Ranges.Single().From!.Value), ranges);
        var c = FileComponent();

        await installer.InstallAsync(c, new Progress<InstallProgress>(), CancellationToken.None);

        Assert.Equal([null, 2000L], ranges);   // 둘째 요청은 받은 데서부터
        Assert.Equal(Body, File.ReadAllBytes(installer.Resolve(c.Target)));
        Assert.False(File.Exists(installer.Resolve(c.Target) + ".part"));
    }

    [Fact]
    public async Task Download_KeepsFailingWithoutProgress_GivesUpAndKeepsThePart()
    {
        using var t = new TempDir();
        var ranges = new List<long?>();
        // 첫 요청만 2000바이트를 주고, 그 뒤로는 한 바이트도 못 받고 끊긴다
        var installer = Make(t, (req, n) => n == 0 ? Respond(0, cut: 2000) : Respond(req.Headers.Range!.Ranges.Single().From!.Value, cut: 0), ranges);
        var c = FileComponent();

        await Assert.ThrowsAsync<IOException>(() => installer.InstallAsync(c, new Progress<InstallProgress>(), CancellationToken.None));

        Assert.Equal(1 + ComponentInstaller.MaxResumes + 1, ranges.Count);   // 진전이 있던 첫 번째는 세지 않는다
        Assert.False(File.Exists(installer.Resolve(c.Target)));
        Assert.Equal(2000, new FileInfo(installer.Resolve(c.Target) + ".part").Length);   // 다음 설치가 이어받는다
    }

    [Fact]
    public async Task Download_ServerSaysNo_FailsAtOnceWithoutRetrying()
    {
        using var t = new TempDir();
        var ranges = new List<long?>();
        var installer = Make(t, (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized), ranges);

        var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            installer.InstallAsync(FileComponent(civitai: true), new Progress<InstallProgress>(), CancellationToken.None));

        Assert.Single(ranges);
        Assert.Contains("civitai", ex.Message);
    }

    [Fact]
    public async Task Download_HtmlPageInsteadOfTheFile_IsRejected()
    {
        // 로그인이 필요한 모델은 200으로 로그인 쪽(HTML)을 주기도 한다 — 그것을 체크포인트 이름으로 저장하면 '설치됨'인데 쓸 수 없다
        using var t = new TempDir();
        var ranges = new List<long?>();
        var installer = Make(t, (_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            return resp;
        }, ranges);
        var c = FileComponent(civitai: true);

        var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(() => installer.InstallAsync(c, new Progress<InstallProgress>(), CancellationToken.None));

        Assert.Single(ranges);
        Assert.Contains("civitai", ex.Message);
        Assert.False(File.Exists(installer.Resolve(c.Target)));
        Assert.False(File.Exists(installer.Resolve(c.Target) + ".part"));
    }

    [Fact]
    public async Task Download_UserCancels_StopsWithoutRetrying()
    {
        using var t = new TempDir();
        var ranges = new List<long?>();
        using var cts = new CancellationTokenSource();
        var installer = Make(t, (_, _) => Respond(0, cut: 2000, stall: true), ranges);
        installer.IdleTimeout = TimeSpan.FromSeconds(30);
        cts.CancelAfter(200);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(FileComponent(), new Progress<InstallProgress>(), cts.Token));
        Assert.Single(ranges);
    }

    [Fact]
    public void Catalog_Checkpoint_HasSha()
    {
        // 6.5GB를 남의 서버에서 받는다 — 깨졌거나 다른 파일이 그 이름으로 저장되지 않게 확인한다
        Assert.Equal("f116b0c78ff441467b0cdc8f1936e1ed18ea31e9997c7b132b1b8db533f0bd04", ComponentCatalog.ModelCheckpoint.Sha256, ignoreCase: true);
        Assert.Equal(6_938_040_682, ComponentCatalog.ModelCheckpoint.ApproxBytes);
    }

    [Fact]
    public void IsInstalled_ComfyPortable_HalfExtracted_IsNotInstalled()
    {
        // 압축을 풀다 꺼지면 main.py는 있는데 python_embeded가 덜 풀렸을 수 있다 — 다 받은 압축 파일이 tmp에 남아 있으면 풀기가 끝나지 않은 것이다
        using var t = new TempDir();
        var installer = new ComponentInstaller(new AppSettings { RuntimeRoot = t.At("rt"), GpuVendor = "nvidia" });
        var c = ComponentCatalog.ComfyPortable;
        RuntimeTestHelpers.Touch(installer.Resolve(c.Probe));
        var archive = RuntimeTestHelpers.Touch(installer.Resolve("tmp/comfyui-nvidia.7z"));
        Assert.False(installer.IsInstalled(c));
        File.Delete(archive);
        Assert.True(installer.IsInstalled(c));
    }
}
