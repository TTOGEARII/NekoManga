namespace NekoManga.Core.Engine;

/// <summary>한 발 앞서 읽어 두는 그릇 — 번역 쪽이 한 쪽의 번역 응답을 기다리는 동안 다음 쪽의 읽기 단계(감지 → 글자 읽기)를 먼저 돌려 둔다(2026-11-27 속도:
/// 응답을 기다리는 2~7초가 가벼운 쪽에서는 그대로 빈 시간이었다). 열쇠는 원고 경로(대소문자를 가리지 않는다).
///
/// 지키는 것 셋:
///  · 읽기 둘이 겹치지 않는다 — 읽기는 같은 감지기·글자 읽기 모델을 쓴다. 다른 쪽을 찾으면 미리 읽던 것이 **끝나기를 기다려** 치운 뒤에 읽는다.
///  · 안 쓰게 된 결과는 치운다(쪽 그림·마스크 Mat을 든다).
///  · 미리 읽기의 실패는 그 쪽의 차례에만 나온다 — 띄울 때(<see cref="Start"/>)는 던지지 않고, 그 쪽을 찾을 때(<see cref="TakeOrReadAsync"/>) 같은 예외로 나온다.
/// 한 흐름이 차례로 부르는 것을 전제로 한다(쪽은 하나씩 번역된다) — 잠금은 띄운 읽기가 다른 스레드에서 끝나는 것만 가린다.</summary>
internal sealed class Prefetched<T> where T : class, IDisposable
{
    private readonly object _gate = new();
    private string? _key;
    private Task<T>? _task;

    /// <summary>이 열쇠의 것을 미리 읽기 시작한다(뒤에서). 이미 같은 열쇠를 읽는 중·읽어 두었으면 그대로 두고, 다른 열쇠의 것이 있으면 그것이 끝난 뒤 치우고 읽는다.</summary>
    public void Start(string key, Func<Task<T>> read)
    {
        lock (_gate)
        {
            if (_task != null && Same(_key, key)) return;
            var before = _task;
            _key = key;
            // Task.Run: 읽기의 앞머리(파일 읽기)도 부른 쪽 스레드에서 돌지 않게, 그리고 read가 바로 던져도 Task의 실패로 남게
            _task = before == null ? Task.Run(read) : Task.Run(() => After(before, read));
            // 끝내 아무도 찾지 않은 미리 읽기의 예외가 '관찰되지 않은 예외'로 남지 않게 본 것으로만 해 둔다(찾는 쪽은 같은 Task를 기다려 그대로 받는다)
            _ = _task.ContinueWith(done => _ = done.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
    }

    /// <summary>이 열쇠의 것을 준다 — 미리 읽어 둔 것이 있으면 그것을(아직 읽는 중이면 끝나기를 기다려), 없으면 지금 읽는다.
    /// 미리 읽던 것이 다른 열쇠의 것이면 그 읽기가 끝난 뒤 치우고 읽는다. 받은 것은 받은 쪽이 치운다(그릇은 비워진다).</summary>
    public Task<T> TakeOrReadAsync(string key, Func<Task<T>> read)
    {
        Task<T>? held;
        bool hit;
        lock (_gate)
        {
            held = _task;
            hit = held != null && Same(_key, key);
            _task = null;
            _key = null;
        }
        if (held == null) return read();
        return hit ? held : After(held, read);
    }

    /// <summary>미리 읽어 둔 것을 버린다 — 읽는 중이면 끝나기를 기다려 치운다(작업이 끝나거나 멈출 때, 모델을 내리기 전에 부른다). 실패한 미리 읽기는 그냥 버린다.</summary>
    public Task DropAsync()
    {
        Task<T>? held;
        lock (_gate)
        {
            held = _task;
            _task = null;
            _key = null;
        }
        return held == null ? Task.CompletedTask : Discard(held);
    }

    private static async Task<T> After(Task<T> before, Func<Task<T>> read)
    {
        await Discard(before);
        return await read();
    }

    private static async Task Discard(Task<T> task)
    {
        try { (await task).Dispose(); }
        catch { }   // 쓰지 않을 쪽의 미리 읽기가 실패·취소됐다 — 버릴 것이 없다(그 쪽을 정말 읽게 되면 그때 같은 까닭으로 실패한다)
    }

    private static bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
