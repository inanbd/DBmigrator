using DbMigrator.Cli;
using DbMigrator.Core.Execution;
using DbMigrator.Tests.Execution;

namespace DbMigrator.Tests.Cli;

/// <summary>The one line the CLI writes to stderr after a cancel must agree with the report above it.</summary>
public class CancelledLineTests
{
    [Fact]
    public async Task Each_phase_gets_its_own_line()
    {
        var harness = await EngineHarness.CreateAsync();
        var plan = harness.Migration.Plan;
        MigrationReport Report(MigrationPhase phase, bool interrupted) => new()
        {
            Plan = plan,
            Tables = [],
            Steps = [],
            Issues = [],
            Elapsed = TimeSpan.FromSeconds(1),
            CancelledDuring = phase,
            InterruptedTable = interrupted ? plan.Tables[1] : null,
        };

        Assert.Equal("Cancelled. Nothing was changed.", CliApp.CancelledLine(Report(MigrationPhase.CreatingTables, false)));
        Assert.Equal("Cancelled. dbo.B was being copied and was rolled back; tables already copied remain.", CliApp.CancelledLine(Report(MigrationPhase.CopyingData, true)));
        Assert.Equal("Cancelled. Every table was copied; see the summary above for what did not run.", CliApp.CancelledLine(Report(MigrationPhase.CreatingConstraints, false)));
    }
}
