using DbMigrator.Core.Execution;
using DbMigrator.Desktop.Testing;
using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Tests;

public sealed class ThroughputEstimatorTests
{
    private readonly ManualTimeProvider _time = new();

    [Fact]
    public void The_eta_appears_only_after_ten_seconds_and_one_percent()
    {
        // Four tables of 1,000 rows: 40 rows is 1%.
        var migration = Plans.Prepare(estimate: 1000);
        var tracker = new RunTracker(migration, _time);
        var estimator = new ThroughputEstimator(_time);
        var a = migration.Table(TestServices.A);
        tracker.Apply(new PhaseStarted(MigrationPhase.CopyingData).At(_time.GetUtcNow()));
        tracker.Apply(new TableCopyStarted(a).At(_time.GetUtcNow()));
        estimator.Update(tracker);

        // Plenty of progress, too soon.
        _time.Advance(TimeSpan.FromSeconds(5));
        tracker.ApplyProgress(new TableCopyProgress(a, 200, TimeSpan.FromSeconds(5)));
        estimator.Update(tracker);
        Assert.Null(estimator.Remaining);

        // Long enough, but less than 1%.
        var slow = new RunTracker(migration, _time);
        var slowEstimator = new ThroughputEstimator(_time);
        slow.Apply(new PhaseStarted(MigrationPhase.CopyingData).At(_time.GetUtcNow()));
        slow.Apply(new TableCopyStarted(a).At(_time.GetUtcNow()));
        slowEstimator.Update(slow);
        _time.Advance(TimeSpan.FromSeconds(11));
        slow.ApplyProgress(new TableCopyProgress(a, 30, TimeSpan.FromSeconds(11)));
        slowEstimator.Update(slow);
        Assert.Null(slowEstimator.Remaining);

        // Both.
        _time.Advance(TimeSpan.FromSeconds(6));
        tracker.ApplyProgress(new TableCopyProgress(a, 440, TimeSpan.FromSeconds(11)));
        estimator.Update(tracker);
        Assert.NotNull(estimator.Remaining);
        Assert.Equal(0.11, estimator.Fraction, 3);
        Assert.True(estimator.RowsPerSecond > 0);
    }

    [Fact]
    public void Tables_without_any_estimates_weigh_the_same()
    {
        var migration = Plans.Prepare(estimate: null);
        var tracker = new RunTracker(migration, _time);
        var estimator = new ThroughputEstimator(_time);
        var a = migration.Table(TestServices.A);
        tracker.Apply(new TableCopyStarted(a).At(_time.GetUtcNow()));
        tracker.Apply(new TableCopyFinished(new TableResult { Table = a, Status = TableStatus.Copied, RowsCopied = 9 }).At(_time.GetUtcNow()));

        estimator.Update(tracker);

        Assert.Equal(0.25, estimator.Fraction, 3);
    }

    [Fact]
    public void A_long_stall_gives_no_estimate_instead_of_overflowing()
    {
        var migration = Plans.Prepare(estimate: 10_000_000);
        var tracker = new RunTracker(migration, _time);
        var estimator = new ThroughputEstimator(_time);
        var a = migration.Table(TestServices.A);
        tracker.Apply(new PhaseStarted(MigrationPhase.CopyingData).At(_time.GetUtcNow()));
        tracker.Apply(new TableCopyStarted(a).At(_time.GetUtcNow()));
        estimator.Update(tracker);
        _time.Advance(TimeSpan.FromSeconds(20));
        tracker.ApplyProgress(new TableCopyProgress(a, 400_000, TimeSpan.FromSeconds(20)));
        estimator.Update(tracker);
        Assert.NotNull(estimator.Remaining);

        // Hours without a single row, ticked four times a second as the screen does.
        for (var i = 0; i < 4 * 60 * 60 * 3; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(250));
            estimator.Update(tracker);
        }

        Assert.Null(estimator.Remaining);
    }

    [Fact]
    public void The_first_rate_is_the_average_so_far_not_one_burst()
    {
        var migration = Plans.Prepare(estimate: 1_000_000);
        var tracker = new RunTracker(migration, _time);
        var estimator = new ThroughputEstimator(_time);
        var a = migration.Table(TestServices.A);
        tracker.Apply(new PhaseStarted(MigrationPhase.CopyingData).At(_time.GetUtcNow()));
        tracker.Apply(new TableCopyStarted(a).At(_time.GetUtcNow()));
        estimator.Update(tracker);

        // 1,000 rows a second, reported once a second, sampled every 250 ms.
        for (var tick = 1; tick <= 12; tick++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(250));
            if (tick % 4 == 0)
            {
                tracker.ApplyProgress(new TableCopyProgress(a, tick / 4 * 1000, TimeSpan.FromSeconds(tick / 4)));
            }

            estimator.Update(tracker);
        }

        Assert.InRange(estimator.RowsPerSecond, 900, 1100);
    }
}
