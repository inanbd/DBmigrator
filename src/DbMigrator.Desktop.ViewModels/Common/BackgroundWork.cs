namespace DbMigrator.Desktop.ViewModels;

/// <summary>
/// The only way view models reach the engine, providers and platform services. Core awaits
/// without ConfigureAwait(false), so a call made directly from the UI thread would resume every
/// continuation there — the whole row loop of a 10 GB copy included. Task.Run starts the work on
/// the thread pool, where there is no UI context to resume on. Tests enforce this rule.
/// </summary>
public static class BackgroundWork
{
    public static Task RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken) =>
        Task.Run(() => work(cancellationToken), cancellationToken);

    public static Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken) =>
        Task.Run(() => work(cancellationToken), cancellationToken);

    public static Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken) =>
        Task.Run(work, cancellationToken);

    public static Task RunAsync(Action work, CancellationToken cancellationToken) =>
        Task.Run(work, cancellationToken);
}
