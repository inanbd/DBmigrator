using DbMigrator.Core.Execution;
using static DbMigrator.Tests.Execution.EngineHarness;

namespace DbMigrator.Tests.Execution;

public class MigrationEngineEventTests
{
    [Fact]
    public async Task Long_phases_announce_each_step_and_count_before_doing_it()
    {
        var harness = await CreateAsync();
        var events = new List<MigrationEvent>();

        await harness.RunAsync(new SyncProgress(events.Add), TestContext.Current.CancellationToken);

        var phases = events.OfType<PhaseStarted>().Select(p => p.Phase);
        Assert.Equal([MigrationPhase.CreatingTables, MigrationPhase.CopyingData, MigrationPhase.CreatingConstraints, MigrationPhase.VerifyingRowCounts], phases);

        var steps = harness.Migration.Script.AfterData;
        var stepEvents = events.Where(e => e is StepStarted or StepFinished).ToList();
        Assert.Equal(steps.Count * 2, stepEvents.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            var started = Assert.IsType<StepStarted>(stepEvents[2 * i]);
            var finished = Assert.IsType<StepFinished>(stepEvents[(2 * i) + 1]);
            Assert.Equal(i, started.Index);
            Assert.Equal(steps.Count, started.Count);
            Assert.Same(started.Step, finished.Result.Step);
        }

        var verifyEvents = events.Where(e => e is TableVerifyStarted or TableVerified).ToList();
        Assert.Equal(8, verifyEvents.Count);
        for (var n = 0; n < 4; n++)
        {
            var started = Assert.IsType<TableVerifyStarted>(verifyEvents[2 * n]);
            var verified = Assert.IsType<TableVerified>(verifyEvents[(2 * n) + 1]);
            Assert.Equal((n, 4), (started.Index, started.Count));
            Assert.Same(started.Table, verified.Result.Table);
            Assert.Equal(5, verified.Result.SourceRowCount);
        }
    }
}
