namespace DbMigrator.Desktop.ViewModels;

/// <summary>
/// Overall copy progress weighted by catalog row estimates, a smoothed rows-per-second rate, and an
/// ETA that is shown only once it means something (after 10 seconds and 1% of the copy).
/// </summary>
public sealed class ThroughputEstimator(TimeProvider time)
{
    private static readonly TimeSpan HalfLife = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MinimumElapsedForEta = TimeSpan.FromSeconds(10);

    private DateTimeOffset? _lastAt;
    private long _lastRows;
    private DateTimeOffset? _copyStartedAt;

    /// <summary>0–1 of the copy, by estimated rows.</summary>
    public double Fraction { get; private set; }

    public double RowsPerSecond { get; private set; }

    /// <summary>The time left in the copy, when it can be estimated.</summary>
    public TimeSpan? Remaining { get; private set; }

    public void Update(RunTracker tracker)
    {
        var now = time.GetUtcNow();
        var rows = tracker.Rows.Where(r => r.Status != TableRunStatus.Skipped).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        // Tables without an estimate weigh the median of the known ones, or 1.
        var known = rows.Where(r => r.EstimatedRows is > 0).Select(r => r.EstimatedRows!.Value).Order().ToList();
        var fallback = known.Count == 0 ? 1 : Math.Max(1, known[known.Count / 2]);
        double total = 0, done = 0;
        foreach (var row in rows)
        {
            double weight = row.EstimatedRows is > 0 and var estimate ? estimate : fallback;
            total += weight;
            done += row.Status switch
            {
                TableRunStatus.Copying => weight * Math.Min((double)row.RowsCopied / weight, 0.99),
                TableRunStatus.Waiting => 0,
                _ => weight,
            };
        }

        Fraction = total > 0 ? done / total : 0;

        var copiedRows = tracker.RowsCopied;
        if (tracker.Phase == Core.Execution.MigrationPhase.CopyingData)
        {
            _copyStartedAt ??= now;
            if (_lastAt is { } lastAt && now > lastAt)
            {
                var seconds = (now - lastAt).TotalSeconds;
                var instant = Math.Max(0, copiedRows - _lastRows) / seconds;
                var alpha = 1 - Math.Pow(2, -seconds / HalfLife.TotalSeconds);
                RowsPerSecond = RowsPerSecond == 0 ? instant : RowsPerSecond + (alpha * (instant - RowsPerSecond));
            }

            var remainingRows = Math.Max(0, total - done);
            Remaining = now - _copyStartedAt >= MinimumElapsedForEta && Fraction >= 0.01 && RowsPerSecond > 0
                ? TimeSpan.FromSeconds(remainingRows / RowsPerSecond)
                : null;
        }
        else
        {
            Remaining = null;
        }

        _lastAt = now;
        _lastRows = copiedRows;
    }
}
