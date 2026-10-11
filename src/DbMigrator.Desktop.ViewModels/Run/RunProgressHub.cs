using DbMigrator.Core.Execution;
using DbMigrator.Core.Schema;

namespace DbMigrator.Desktop.ViewModels;

/// <summary>An engine event and when it arrived.</summary>
public sealed record TimedEvent(MigrationEvent Event, DateTimeOffset At);

/// <param name="Discrete">Events in arrival order, at most the number asked for.</param>
/// <param name="Latest">The newest progress per table still copying; earlier progress was dropped.</param>
/// <param name="More">True when discrete events are left for the next drain.</param>
public sealed record ProgressBatch(IReadOnlyList<TimedEvent> Discrete, IReadOnlyList<TableCopyProgress> Latest, bool More);

/// <summary>
/// Collects engine events from the engine's thread; the UI pulls them on a timer. Row progress is
/// coalesced to the newest per table, so the UI's work per tick is bounded however fast the engine
/// reports. Nothing here touches the UI.
/// </summary>
public sealed class RunProgressHub(TimeProvider time) : IProgress<MigrationEvent>
{
    private readonly Lock _lock = new();
    private readonly Dictionary<QualifiedName, TableCopyProgress> _latest = [];
    private List<TimedEvent> _discrete = [];

    public void Report(MigrationEvent value)
    {
        lock (_lock)
        {
            if (value is TableCopyProgress progress)
            {
                _latest[progress.Table.Source.Name] = progress;
                return;
            }

            if (value is TableCopyFinished finished)
            {
                // A finished table's last progress is superseded by its result.
                _latest.Remove(finished.Result.Table.Source.Name);
            }

            _discrete.Add(new TimedEvent(value, time.GetUtcNow()));
        }
    }

    public ProgressBatch Drain(int maxDiscrete)
    {
        lock (_lock)
        {
            List<TimedEvent> taken;
            if (_discrete.Count <= maxDiscrete)
            {
                taken = _discrete;
                _discrete = [];
            }
            else
            {
                taken = _discrete.GetRange(0, maxDiscrete);
                _discrete.RemoveRange(0, maxDiscrete);
            }

            var latest = _latest.Values.ToList();
            _latest.Clear();
            return new ProgressBatch(taken, latest, _discrete.Count > 0);
        }
    }
}
