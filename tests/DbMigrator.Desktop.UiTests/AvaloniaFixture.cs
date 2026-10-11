using Avalonia;
using Avalonia.Headless;
using Avalonia.Logging;
using DbMigrator.Desktop.UiTests;

[assembly: AssemblyFixture(typeof(AvaloniaFixture))]
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace DbMigrator.Desktop.UiTests;

/// <summary>The real app, rendered by Skia without a screen, so screenshots show what users see.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// One headless Avalonia session for the assembly. All Avalonia access goes through
/// <see cref="RunAsync(Func{Task})"/>. The session is never disposed: its Dispose does not return under xUnit.
/// </summary>
public sealed class AvaloniaFixture : IAsyncLifetime
{
    private HeadlessUnitTestSession? _session;

    public BindingErrorSink Bindings { get; } = new();

    public async ValueTask InitializeAsync()
    {
        _session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder), AvaloniaTestIsolationLevel.PerAssembly);

        // Bind the dispatcher to the session thread before any test can touch it.
        await _session.Dispatch(() => Logger.Sink = Bindings, CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
    }

    public async Task RunAsync(Func<Task> test) =>
        await _session!.Dispatch(
                async () =>
                {
                    await test();
                    return true;
                },
                TestContext.Current.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Collects binding warnings, which compiled bindings still report at run time (e.g. a null in a path).</summary>
public sealed class BindingErrorSink : ILogSink
{
    private readonly List<string> _errors = [];

    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_errors)
            {
                return _errors.ToList();
            }
        }
    }

    public void Clear()
    {
        lock (_errors)
        {
            _errors.Clear();
        }
    }

    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area == LogArea.Binding;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Add(messageTemplate);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
        Add($"{source?.GetType().Name}: {messageTemplate} | {string.Join(" | ", propertyValues)}");

    private void Add(string error)
    {
        lock (_errors)
        {
            _errors.Add(error);
        }
    }
}
