namespace DbMigrator.Core.Execution;

/// <summary>
/// Thrown when a running migration is cancelled. It is an <see cref="OperationCanceledException"/>,
/// so callers that only care about cancellation keep working, and it carries a report that
/// accounts for every planned table and step.
/// </summary>
public sealed class MigrationCancelledException : OperationCanceledException
{
    public MigrationCancelledException(MigrationReport partialReport, Exception? innerException, CancellationToken cancellationToken)
        : base("The migration was cancelled.", innerException, cancellationToken)
    {
        PartialReport = partialReport;
    }

    public MigrationReport PartialReport { get; }
}
