using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace DbMigrator.TestSupport;

/// <summary>
/// Records calls that arrive on a thread they must not use, such as the UI thread. Fakes call
/// <see cref="Check"/> at every I/O entry point; tests assert <see cref="Violations"/> is empty.
/// </summary>
public sealed class ThreadGuard
{
    private readonly ConcurrentQueue<string> _violations = new();

    /// <summary>Returns true when the current thread is forbidden. Null disables the guard.</summary>
    public Func<bool>? IsForbidden { get; set; }

    public IReadOnlyCollection<string> Violations => _violations;

    public void Check([CallerMemberName] string caller = "")
    {
        if (IsForbidden?.Invoke() == true)
        {
            _violations.Enqueue($"{caller} on thread {Environment.CurrentManagedThreadId}");
        }
    }
}
