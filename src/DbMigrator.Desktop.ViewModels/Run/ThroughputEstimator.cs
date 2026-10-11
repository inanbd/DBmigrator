namespace DbMigrator.Desktop.ViewModels;

/// <summary>
/// Overall copy progress weighted by catalog row estimates, a smoothed rows-per-second rate, and an
/// ETA that is shown only once it means something (after 10 seconds and 1% of the copy).
/// </summary>
public sealed class ThroughputEstimator(TimeProvider time)
{
    private static readonly TimeSpan HalfLife = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MinimumElapsedForEta = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The engine reports a table's rows about once a second, so the rate is measured over spans
    /// at least this long; a span of one 250 ms tick would swing between zero and four times the rate.
    /// </summary>
    private static readonly TimeSpan SampleSpan = TimeSpan.FromSeconds(2);

    /// <summary>Longer than this is not an estimate anyone can use (and would overflow a TimeSpan before long).</summary>
    private static readonly TimeSpan MaximumEta = TimeSpan.FromDays(365);

    /// <summary>Below this the copy is stalled, and an estimate would be meaningless (or overflow).</summary>
    private const double MinimumRate = 0.01;

    private DateTimeOffset? _sampledAt;
    private long _sampledRows;
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

                // Not reached, or rolled back by a stop: none of it is done.
                TableRunStatus.Waiting or TableRunStatus.NotStarted or TableRunStatus.RolledBack => 0,
                _ => weight,
            };
        }

        Fraction = total > 0 ? done / total : 0;

        var copiedRows = tracker.RowsCopied;
        if (tracker.Phase != Core.Execution.MigrationPhase.CopyingData)
        {
            Remaining = null;
            return;
        }

        if (_copyStartedAt is not { } copyStarted)
        {
            _copyStartedAt = now;
            _sampledAt = now;
            _sampledRows = copiedRows;
            Remaining = null;
            return;
        }

        if (_sampledAt is { } sampledAt && now - sampledAt >= SampleSpan)
        {
            var seconds = (now - sampledAt).TotalSeconds;
            var recent = Math.Max(0, copiedRows - _sampledRows) / seconds;
            var alpha = 1 - Math.Pow(2, -seconds / HalfLife.TotalSeconds);

            // The first rate is the average so far, not one span's burst.
            RowsPerSecond = RowsPerSecond == 0 ? copiedRows / (now - copyStarted).TotalSeconds : RowsPerSecond + (alpha * (recent - RowsPerSecond));
            _sampledAt = now;
            _sampledRows = copiedRows;
        }

        Remaining = null;
        if (now - copyStarted >= MinimumElapsedForEta && Fraction >= 0.01 && RowsPerSecond >= MinimumRate)
        {
            // Rows left, in the same estimated units as the fraction: the copy's rows per estimated row.
            var remainingRows = Math.Max(0, total - done) * (copiedRows > 0 && done > 0 ? copiedRows / done : 1);
            var remaining = remainingRows / RowsPerSecond;
            if (double.IsFinite(remaining) && remaining < MaximumEta.TotalSeconds)
            {
                Remaining = TimeSpan.FromSeconds(remaining);
            }
        }
    }
}
