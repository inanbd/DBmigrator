namespace DbMigrator.Desktop.ViewModels;

/// <summary>
/// Runs work after a quiet period, and only applies the newest result: each call bumps a
/// generation, cancels the previous wait, and a result from an older generation is dropped.
/// Call from the UI thread; <c>apply</c> runs there too.
/// </summary>
public sealed class Debouncer(TimeProvider time, TimeSpan delay)
{
    private int _generation;
    private CancellationTokenSource? _pending;

    public int Generation => _generation;

    /// <returns>True when this call's result was applied.</returns>
    public async Task<bool> RunAsync<T>(Func<CancellationToken, Task<T>> work, Action<T> apply)
    {
        // Everything up to the wait happens before the first await: a call that yielded here could
        // replace a newer call's token source, or start its wait after the clock had moved on.
        var generation = ++_generation;
        var pending = new CancellationTokenSource();
        var previous = _pending;
        _pending = pending;
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, time, pending.Token);
            }

            if (generation != _generation)
            {
                return false;
            }

            var result = await work(pending.Token);
            if (generation != _generation)
            {
                return false;
            }

            apply(result);
            return true;
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Drops whatever is pending, so no older result will be applied.</summary>
    public void Cancel()
    {
        _generation++;
        _pending?.Cancel();
    }
}
