namespace DbMigrator.TestSupport;

/// <summary>
/// A pause point inside a fake: work that reaches it signals <see cref="Reached"/> and waits
/// until the test calls <see cref="Release"/> (or the work is cancelled). Tests use gates instead
/// of delays, so they never depend on timing.
/// </summary>
public sealed class Gate
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Reached => _reached.Task;

    public bool IsReleased => _released.Task.IsCompleted;

    public void Release() => _released.TrySetResult();

    public async Task PassAsync(CancellationToken cancellationToken)
    {
        _reached.TrySetResult();
        await _released.Task.WaitAsync(cancellationToken);
    }
}
