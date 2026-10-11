using System.Collections.Concurrent;

namespace DbMigrator.Desktop.Testing;

/// <summary>
/// A strict single-threaded SynchronizationContext standing in for the UI thread in view-model tests.
/// Fakes call <see cref="IsOnUiThread"/> to prove engine I/O never runs here.
/// </summary>
public sealed class UiThreadContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

    [ThreadStatic]
    private static bool t_isUiThread;

    public static bool IsOnUiThread => t_isUiThread;

    /// <summary>Queues work for the fake UI thread. Work posted after the test has ended is dropped.</summary>
    public override void Post(SendOrPostCallback d, object? state)
    {
        try
        {
            _queue.Add((d, state));
        }
        catch (InvalidOperationException) when (_queue.IsAddingCompleted)
        {
        }
    }

    public override void Send(SendOrPostCallback d, object? state) =>
        throw new InvalidOperationException("Synchronous Send to the UI thread would deadlock a real app.");

    public override SynchronizationContext CreateCopy() => this;

    public static Task RunAsync(Func<Task> body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var context = new UiThreadContext();
            SetSynchronizationContext(context);
            t_isUiThread = true;
            Task task;
            try
            {
                task = body();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
                return;
            }

            task.ContinueWith(_ => context._queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (callback, state) in context._queue.GetConsumingEnumerable())
            {
                callback(state);
            }

            if (task.IsFaulted)
            {
                done.SetException(task.Exception!.InnerExceptions);
            }
            else if (task.IsCanceled)
            {
                done.SetCanceled();
            }
            else
            {
                done.SetResult();
            }
        })
        { IsBackground = true, Name = "fake UI thread" };
        thread.Start();
        return done.Task;
    }
}
