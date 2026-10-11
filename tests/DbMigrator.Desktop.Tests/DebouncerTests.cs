using DbMigrator.Desktop.Testing;
using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Tests;

public sealed class DebouncerTests
{
    [Fact]
    public Task A_late_result_never_overwrites_a_newer_one() => UiThreadContext.RunAsync(async () =>
    {
        var time = new ManualTimeProvider();
        var debouncer = new Debouncer(time, TimeSpan.FromMilliseconds(200));
        var applied = new List<string>();
        var slow = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = debouncer.RunAsync(_ => slow.Task, applied.Add);
        time.Advance(TimeSpan.FromMilliseconds(200));
        await Wait.UntilAsync(() => debouncer.Generation == 1, "the first wait");

        // The first plan is still working when the second is asked for.
        await Task.Delay(20);
        var second = debouncer.RunAsync(_ => Task.FromResult("new"), applied.Add);
        await Task.Delay(20);
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.True(await second);

        slow.SetResult("old");
        Assert.False(await first);
        Assert.Equal(["new"], applied);
    });

    [Fact]
    public Task Calls_within_the_quiet_period_run_only_the_last() => UiThreadContext.RunAsync(async () =>
    {
        var time = new ManualTimeProvider();
        var debouncer = new Debouncer(time, TimeSpan.FromMilliseconds(200));
        var runs = 0;
        var calls = Enumerable.Range(0, 5).Select(i => debouncer.RunAsync(_ => Task.FromResult(Interlocked.Increment(ref runs)), _ => { })).ToList();
        await Task.Delay(20);
        time.Advance(TimeSpan.FromMilliseconds(200));

        var results = await Task.WhenAll(calls);

        Assert.Equal([false, false, false, false, true], results);
        Assert.Equal(1, runs);
    });
}
