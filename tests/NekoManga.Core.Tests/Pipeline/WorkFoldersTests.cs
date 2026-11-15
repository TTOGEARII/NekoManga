using NekoManga.Core.Pipeline;
using static NekoManga.Core.Tests.PipelineTestHelpers;

namespace NekoManga.Core.Tests;

/// <summary>작업 폴더와 ComfyUI 파일의 뒷정리(WorkFolders) — 끝난 작업의 폴더는 지우고, 오래 남은 것은 앱이 시작할 때 지운다(2026-11-13 점검 12번: 641개 6.0GB가 쌓여 있었다).
/// 지우는 코드라서 무엇을 **안** 지우는지를 같이 붙잡는다. 러너는 시험 이음새(WorkRootOverride)로 임시 폴더 안에서 돈다 — 실제 %AppData%는 건드리지 않는다.</summary>
public class WorkFoldersTests
{
    private static string JobDir(TempDir t, string name, int ageDays, bool withFiles = true)
    {
        var dir = t.At("work", name);
        Directory.CreateDirectory(dir);
        if (withFiles) { WriteBytesFile(Path.Combine(dir, "0000_01.inpainted.png"), 32); WriteBytesFile(Path.Combine(dir, "sub", "x.bin"), 8); }
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddDays(-ageDays));
        return dir;
    }

    // ── 오래된 작업 폴더 훑기 ──

    [Fact]
    public void SweepOld_RemovesOnlyJobFoldersPastTheLimit()
    {
        using var t = new TempDir();
        var old = JobDir(t, "job-20261001-101010", ageDays: 30);
        var oldNewName = JobDir(t, "job-20261101-101010-ab12", ageDays: 8);
        var recent = JobDir(t, "job-20261112-101010-cd34", ageDays: 6);      // 다른 창이 돌리는 중이거나 검토 대기로 열어 둔 작업일 수 있다
        var notAJob = JobDir(t, "corrections", ageDays: 30);                 // 이름이 job-이 아닌 폴더
        var looseFile = WriteBytesFile(t.At("work", "job-notes.txt"), 8);    // 폴더가 아닌 것
        File.SetLastWriteTimeUtc(looseFile, DateTime.UtcNow.AddDays(-30));

        int removed = WorkFolders.SweepOld(t.At("work"), TimeSpan.FromDays(7));

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(old));
        Assert.False(Directory.Exists(oldNewName));
        Assert.True(File.Exists(Path.Combine(recent, "0000_01.inpainted.png")));
        Assert.True(File.Exists(Path.Combine(notAJob, "0000_01.inpainted.png")));
        Assert.True(File.Exists(looseFile));
    }

    [Fact]
    public void SweepOld_FolderThatCannotBeRemoved_DoesNotStopTheRest()
    {
        using var t = new TempDir();
        var held = JobDir(t, "job-20261001-101010", ageDays: 30);
        var free = JobDir(t, "job-20261002-101010", ageDays: 30);
        int removed;
        using (new FileStream(Path.Combine(held, "0000_01.inpainted.png"), FileMode.Open, FileAccess.Read, FileShare.None))
            removed = WorkFolders.SweepOld(t.At("work"), TimeSpan.FromDays(7));

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(free));
        Assert.True(Directory.Exists(held));   // 다음 시작 때 다시 지운다
    }

    [Fact]
    public void SweepOld_NoWorkFolderYet_DoesNothing()
    {
        using var t = new TempDir();
        Assert.Equal(0, WorkFolders.SweepOld(t.At("아직 없는 work"), TimeSpan.FromDays(7)));
        Assert.False(Directory.Exists(t.At("아직 없는 work")));
    }

    // ── 끝난 작업의 폴더 ──

    [Fact]
    public void NewJobDir_TwoJobsStartedInTheSameSecond_GetDifferentFolders()
    {
        // 끝난 작업이 제 폴더를 지운다 — 한 폴더를 같이 쓰면 먼저 끝난 쪽이 아직 도는 쪽의 중간 그림을 지운다
        using var t = new TempDir();
        var dirs = Enumerable.Range(0, 20).Select(_ => WorkFolders.NewJobDir(t.At("work"))).ToList();
        Assert.Equal(20, dirs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(dirs, d => Assert.StartsWith("job-", Path.GetFileName(d)));
        Assert.All(dirs, d => Assert.Equal(t.At("work"), Path.GetDirectoryName(d)));
    }

    [Fact]
    public void Remove_DeletesAJobFolderWithEverythingInIt_ButNeverAFolderNamedOtherwise()
    {
        using var t = new TempDir();
        var job = JobDir(t, "job-20261113-101010-ab12", ageDays: 0);
        var other = JobDir(t, "training", ageDays: 0);

        WorkFolders.Remove(job);
        WorkFolders.Remove(job);                  // 이미 없는 폴더 — 던지지 않는다
        WorkFolders.Remove(other);
        WorkFolders.Remove(t.At("work"));         // 작업 폴더들의 상위 폴더

        Assert.False(Directory.Exists(job));
        Assert.True(File.Exists(Path.Combine(other, "0000_01.inpainted.png")));
        Assert.True(Directory.Exists(t.At("work")));
    }

    // ── 러너: 작업이 끝나면 제 폴더를 지운다 ──

    /// <summary>보고가 올 때마다 그 작업의 폴더(러너의 시험 이음새 LastWorkDir)를 적어 두고, 처음 본 폴더에 중간 그림 구실의 파일을 넣는다
    /// — 엔진 없는 설정에서는 번역·검열 제거가 돌지 않아 러너가 중간 그림을 쓰지 않는다.</summary>
    private sealed class WorkWatcher(Func<string?> workDir, Action? onFirstReport = null) : IProgress<PageProgress>
    {
        public HashSet<string> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);
        public void Report(PageProgress value)
        {
            if (workDir() is not { } d || !Seen.Add(d)) return;
            WriteBytesFile(Path.Combine(d, "0000_01.inpainted.png"), 16);
            WriteBytesFile(Path.Combine(d, "sub", "중간 그림.png"), 16);
            onFirstReport?.Invoke();
        }
    }

    [Fact]
    public async Task RunAsync_WorkFolderExistsWhileTheJobRuns_AndIsGoneWhenItEnds()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var src = WriteImageFile(t.At("vol1", "01.jpg"), seed: 1);
        using (var runner = new PipelineRunner(s) { WorkRootOverride = t.At("work") })
        {
            var watcher = new WorkWatcher(() => runner.LastWorkDir);
            await runner.RunAsync([src], watcher, CancellationToken.None);
            Assert.StartsWith(t.At("work", "job-"), Assert.Single(watcher.Seen));   // 전제: 도는 동안 작업 폴더가 임시 폴더 안에 있었다
        }

        Assert.Empty(Directory.GetDirectories(t.At("work")));
        Assert.True(File.Exists(PipelineRunner.AsIsPathFor(PipelineRunner.OutputPathFor(s, src), src)));   // 결과물은 그대로
    }

    [Fact]
    public async Task RunAsync_Stopped_StillRemovesItsWorkFolder()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var a = WriteImageFile(t.At("vol1", "01.jpg"), seed: 1);
        var b = WriteImageFile(t.At("vol1", "02.jpg"), seed: 2);
        using var cts = new CancellationTokenSource();
        using (var runner = new PipelineRunner(s) { WorkRootOverride = t.At("work") })
        {
            var watcher = new WorkWatcher(() => runner.LastWorkDir, cts.Cancel);   // 첫 장의 보고가 온 자리에서 [멈추기]
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync([a, b], watcher, cts.Token));
            Assert.Single(watcher.Seen);
        }

        Assert.Empty(Directory.GetDirectories(t.At("work")));
    }

    [Fact]
    public async Task RedoPagesAsync_AndRedoPageAsync_RemoveTheirWorkFolders()
    {
        using var t = new TempDir(); var s = EnginelessSettings(t);
        var src = WriteImageFile(t.At("vol1", "01.jpg"), seed: 1);
        var outPath = PipelineRunner.OutputPathFor(s, src);
        using (var mask = RectMask(60, 40, new OpenCvSharp.Rect(5, 5, 10, 10)))
        using (var pre = ColorImage(60, 40, seed: 4))
            MaskSidecar.Write(outPath, src, mask, null, pre, "감지", skip: true, edited: true, confirmed: true);
        using (var runner = new PipelineRunner(s) { WorkRootOverride = t.At("work") })
        {
            var watcher = new WorkWatcher(() => runner.LastWorkDir);
            await runner.RedoPagesAsync([(src, outPath)], watcher, CancellationToken.None, new PipelineRunner.RedoPageOptions(DecensorAndLetterOnly: true));
            Assert.Empty(Directory.GetDirectories(t.At("work")));
            await runner.RedoPageAsync(src, watcher, CancellationToken.None, outPath);
            Assert.Empty(Directory.GetDirectories(t.At("work")));
            Assert.Equal(2, watcher.Seen.Count);   // 전제: 두 번 다 작업 폴더가 있었다(서로 다른 폴더)
        }
    }

    // ── ComfyUI에 올리고 받은 파일 ──

    [Fact]
    public void RemoveComfyFiles_DeletesOnlyOurFilesInsideThatComfyUI()
    {
        using var t = new TempDir();
        var root = t.At("ComfyUI_windows_portable");
        string In(string n) => WriteBytesFile(Path.Combine(root, "ComfyUI", "input", n), 8);
        string Out(string n) => WriteBytesFile(Path.Combine(root, "ComfyUI", "output", n), 8);
        var ourIn = In("nm_regen_3_0.png"); var ourIn2 = In("nm_regenmask_3_0.png");
        var userIn = In("my_photo.png"); var nestedIn = In(Path.Combine("pasted", "nm_x.png"));
        var ourOut = Out("nm_regenout_3_0_00001_.png"); var ourOutSub = Out(Path.Combine("sub", "nm_color_3_00001_.png"));
        var userOut = Out("ComfyUI_00012_.png");
        var outside = WriteBytesFile(t.At("다른 폴더", "nm_keep.png"), 8);

        WorkFolders.RemoveComfyFiles(root,
            [ourOut, ourOutSub, userOut, outside, Path.Combine(root, "ComfyUI", "output", "..", "input", "my_photo.png")], null,
            "nm_regen_3_0.png", "nm_regenmask_3_0.png", "my_photo.png", Path.Combine("pasted", "nm_x.png"), Path.Combine("..", "..", "..", "다른 폴더", "nm_keep.png"), null, "");

        Assert.False(File.Exists(ourIn)); Assert.False(File.Exists(ourIn2));
        Assert.False(File.Exists(ourOut)); Assert.False(File.Exists(ourOutSub));
        Assert.True(File.Exists(userIn), "사용자의 입력 파일");
        Assert.True(File.Exists(nestedIn), "input의 하위 폴더는 우리가 쓰는 자리가 아니다");
        Assert.True(File.Exists(userOut), "사용자의 출력 파일");
        Assert.True(File.Exists(outside), "ComfyUI 폴더 밖");
    }

    [Fact]
    public void RemoveComfyFiles_OutputPrefix_SweepsThatPromptsFilesEvenWhenTheResultWasNeverRead()
    {
        // 멈춤·제한 시간 초과·연결 오류로 기다리기를 그만두면 출력 파일 이름을 못 받는다 — 그사이 서버가 저장했으면 남는다. 그 프롬프트의 접두사로 훑는다
        using var t = new TempDir();
        var root = t.At("comfy");
        string Out(string n) => WriteBytesFile(Path.Combine(root, "ComfyUI", "output", n), 8);
        var mine1 = Out("nm_regenout_ab12_3_00001_.png"); var mine2 = Out("nm_regenout_ab12_3_00002_.png");
        var nextPrompt = Out("nm_regenout_ab12_30_00001_.png");    // 같은 러너의 다른 프롬프트(30번) — 3번의 접두사에 걸리면 안 된다
        var otherRunner = Out("nm_regenout_cd34_3_00001_.png");
        var users = Out("ComfyUI_00001_.png");
        var nested = Out(Path.Combine("sub", "nm_regenout_ab12_3_00001_.png"));   // 우리 접두사는 output 바로 아래에만 쓴다

        WorkFolders.RemoveComfyFiles(root, null, "nm_regenout_ab12_3");

        Assert.False(File.Exists(mine1)); Assert.False(File.Exists(mine2));
        Assert.True(File.Exists(nextPrompt)); Assert.True(File.Exists(otherRunner)); Assert.True(File.Exists(users)); Assert.True(File.Exists(nested));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nm_")]            // 너무 짧다 — 앱이 만든 것을 통째로 훑게 된다
    [InlineData("ComfyUI")]        // nm_로 시작하지 않는다
    [InlineData("nm_*")]           // 와일드카드
    [InlineData("nm_regen*")]      // 길이는 넉넉한 와일드카드 — 다른 러너·다른 프롬프트의 출력까지 걸린다
    [InlineData("nm_regenout_????_3")]
    [InlineData("nm_regenout_ab12_3/../x")]
    public void RemoveComfyFiles_SuspiciousOutputPrefix_SweepsNothing(string prefix)
    {
        using var t = new TempDir();
        var root = t.At("comfy");
        var a = WriteBytesFile(Path.Combine(root, "ComfyUI", "output", "nm_regenout_ab12_3_00001_.png"), 8);
        var b = WriteBytesFile(Path.Combine(root, "ComfyUI", "output", "ComfyUI_00001_.png"), 8);
        var c = WriteBytesFile(Path.Combine(root, "ComfyUI", "x_00001_.png"), 8);

        WorkFolders.RemoveComfyFiles(root, null, prefix);

        Assert.True(File.Exists(a)); Assert.True(File.Exists(b)); Assert.True(File.Exists(c));
    }
}

/// <summary>NEKO_KEEP_WORK=1(진단용) — 아무것도 지우지 않는다. 환경변수를 바꾸므로 따로 묶는다.</summary>
[Collection(EnvCollection.Name)]
public class WorkFoldersKeepTests
{
    [Fact]
    public void KeepWork_NothingIsRemoved()
    {
        using var t = new TempDir();
        var job = t.At("work", "job-20261001-101010-ab12");
        WriteBytesFile(Path.Combine(job, "x.png"), 8);
        var comfyIn = WriteBytesFile(t.At("comfy", "ComfyUI", "input", "nm_regen_1_0.png"), 8);

        RuntimeTestHelpers.WithEnv("NEKO_KEEP_WORK", "1", () =>
        {
            Assert.True(WorkFolders.Keep);
            WorkFolders.Remove(job);
            WorkFolders.RemoveComfyFiles(t.At("comfy"), null, null, "nm_regen_1_0.png");
        });

        Assert.True(File.Exists(Path.Combine(job, "x.png")));
        Assert.True(File.Exists(comfyIn));
        Assert.False(RuntimeTestHelpers.WithEnv("NEKO_KEEP_WORK", null, () => WorkFolders.Keep));
    }
}
