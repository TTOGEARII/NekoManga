using NekoManga.Core.Engine;

namespace NekoManga.Core.Tests;

/// <summary>다음 쪽 미리 읽기의 그릇(2026-11-27 속도 — 번역 응답을 기다리는 쪽마다 2~7초 동안 다음 쪽의 감지·글자 읽기를 먼저 돌린다).
/// 지킬 것: 같은 쪽은 한 번만 읽는다 · 읽기 둘이 겹치지 않는다(같은 감지기·글자 읽기 모델을 쓴다) · 안 쓰게 된 것은 치운다(쪽 그림 Mat) · 미리 읽기의 실패는 그 쪽의 차례에만 나온다.</summary>
public class PrefetchedTests
{
    private sealed class Page(string name) : IDisposable
    {
        public string Name => name;
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    /// <summary>끝나는 때를 시험이 정하는 읽기 — 몇 번 불렸는지, 동시에 몇 개가 돌았는지를 센다.</summary>
    private sealed class Reader
    {
        public int Calls, Running, MaxRunning;
        public readonly Dictionary<string, TaskCompletionSource<Page>> Gates = new();
        private readonly object _sync = new();

        public Func<Task<Page>> Read(string name, bool hold = false) => async () =>
        {
            TaskCompletionSource<Page>? gate = null;
            lock (_sync)
            {
                Calls++;
                MaxRunning = Math.Max(MaxRunning, ++Running);
                if (hold) Gates[name] = gate = new TaskCompletionSource<Page>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            try { return gate != null ? await gate.Task : new Page(name); }
            finally { lock (_sync) Running--; }
        };
    }

    private static async Task<T> Soon<T>(Task<T> task)
    {
        // 영영 안 끝나는 것을 기다리지 않는다 — 30초 뒤 실패
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30))));
        return await task;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 3000 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task ThePrefetchedPage_IsReadOnlyOnce()
    {
        var ahead = new Prefetched<Page>();
        var reader = new Reader();

        ahead.Start(@"C:\book\02.png", reader.Read("02"));
        var page = await Soon(ahead.TakeOrReadAsync(@"c:\BOOK\02.png", reader.Read("02 again")));   // 경로는 대소문자를 가리지 않는다

        Assert.Equal("02", page.Name);
        Assert.Equal(1, reader.Calls);
        Assert.False(page.Disposed);   // 넘겨준 것은 받은 쪽이 치운다
    }

    [Fact]
    public async Task WithoutAPrefetch_ItJustReads()
    {
        var ahead = new Prefetched<Page>();
        var page = await Soon(ahead.TakeOrReadAsync("a", new Reader().Read("a")));
        Assert.Equal("a", page.Name);
    }

    [Fact]
    public async Task AnotherPage_DisposesThePrefetchedOne_AndIsReadAfterItFinished()
    {
        // 앞 장이 번역 전에 실패해 미리 읽어 둔 쪽을 건너뛴 경우 — 미리 읽던 것이 끝난 뒤에 치우고 새로 읽는다(읽기 둘이 겹치면 같은 모델을 두 스레드가 쓴다)
        var ahead = new Prefetched<Page>();
        var reader = new Reader();
        ahead.Start("02", reader.Read("02", hold: true));
        await Until(() => reader.Gates.ContainsKey("02"));

        var third = ahead.TakeOrReadAsync("03", reader.Read("03"));
        await Task.Delay(100);
        Assert.False(third.IsCompleted);          // 02의 읽기가 아직 돈다
        Assert.Equal(1, reader.Calls);

        var second = new Page("02");
        reader.Gates["02"].SetResult(second);
        var page = await Soon(third);

        Assert.Equal("03", page.Name);
        Assert.True(second.Disposed);
        Assert.Equal(1, reader.MaxRunning);
    }

    [Fact]
    public async Task AFailedPrefetch_ThrowsOnItsOwnTurn_NotWhenItWasStarted()
    {
        var ahead = new Prefetched<Page>();
        ahead.Start("02", () => throw new InvalidDataException("깨진 그림"));   // Start는 던지지 않는다 — 앞 쪽을 실패로 만들지 않는다

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Soon(ahead.TakeOrReadAsync("02", new Reader().Read("02"))));
        Assert.Equal("깨진 그림", ex.Message);
    }

    [Fact]
    public async Task AFailedPrefetch_DoesNotBreakAnotherPage()
    {
        var ahead = new Prefetched<Page>();
        ahead.Start("02", () => Task.FromException<Page>(new InvalidDataException("깨진 그림")));
        var page = await Soon(ahead.TakeOrReadAsync("03", new Reader().Read("03")));
        Assert.Equal("03", page.Name);
    }

    [Fact]
    public async Task StartingTheSamePageTwice_ReadsOnce_AndAnotherPageReplacesIt()
    {
        var ahead = new Prefetched<Page>();
        var reader = new Reader();
        ahead.Start("02", reader.Read("02", hold: true));
        await Until(() => reader.Gates.ContainsKey("02"));
        ahead.Start("02", reader.Read("02 again"));
        Assert.Equal(1, reader.Calls);

        ahead.Start("03", reader.Read("03"));       // 02가 끝난 뒤에 읽기 시작한다
        await Task.Delay(100);
        Assert.Equal(1, reader.Calls);
        var second = new Page("02");
        reader.Gates["02"].SetResult(second);

        var page = await Soon(ahead.TakeOrReadAsync("03", reader.Read("03 again")));
        Assert.Equal("03", page.Name);
        Assert.True(second.Disposed);
        Assert.Equal(2, reader.Calls);
        Assert.Equal(1, reader.MaxRunning);
    }

    [Fact]
    public async Task Drop_DisposesWhatWasRead_AndWaitsForAReadInFlight()
    {
        // 작업이 끝나거나 멈출 때 — 미리 읽던 것이 끝나기를 기다려 치운다(모델을 내리기 전에 읽기가 끝나 있어야 한다)
        var ahead = new Prefetched<Page>();
        var reader = new Reader();
        ahead.Start("02", reader.Read("02", hold: true));
        await Until(() => reader.Gates.ContainsKey("02"));

        var drop = ahead.DropAsync();
        await Task.Delay(100);
        Assert.False(drop.IsCompleted);

        var second = new Page("02");
        reader.Gates["02"].SetResult(second);
        Assert.Same(drop, await Task.WhenAny(drop, Task.Delay(TimeSpan.FromSeconds(30))));
        await drop;
        Assert.True(second.Disposed);

        await ahead.DropAsync();   // 비어 있으면 아무 일도 없다
        // 치운 뒤에는 새로 읽는다
        Assert.Equal("02 new", (await Soon(ahead.TakeOrReadAsync("02", reader.Read("02 new")))).Name);
    }

    [Fact]
    public async Task Drop_SwallowsAFailedPrefetch()
    {
        var ahead = new Prefetched<Page>();
        ahead.Start("02", () => Task.FromException<Page>(new InvalidDataException("깨진 그림")));
        await ahead.DropAsync();   // 던지지 않는다 — 쓰지 않을 쪽의 실패다
    }
}
