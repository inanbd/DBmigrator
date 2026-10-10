using DbMigrator.Providers.PostgreSql;

namespace DbMigrator.Tests.PostgreSql;

public class PostgreSqlBulkWriterTests
{
    [Fact]
    public void Timestamps_are_microseconds_since_2000_rounded_down()
    {
        Assert.Equal(0, PostgreSqlBulkWriter.MicrosecondsSince2000(new DateTime(2000, 1, 1).Ticks));
        Assert.Equal(1, PostgreSqlBulkWriter.MicrosecondsSince2000(new DateTime(2000, 1, 1).Ticks + 19));
        // Half a microsecond before 2000 stays in 1999.
        Assert.Equal(-1, PostgreSqlBulkWriter.MicrosecondsSince2000(new DateTime(2000, 1, 1).Ticks - 5));
        Assert.Equal(-63082281600000000, PostgreSqlBulkWriter.MicrosecondsSince2000(DateTime.MinValue.Ticks));
        Assert.Equal(252455615999999999, PostgreSqlBulkWriter.MicrosecondsSince2000(DateTime.MaxValue.Ticks));
    }

    [Fact]
    public void Dates_are_days_since_2000()
    {
        Assert.Equal(0, PostgreSqlBulkWriter.DaysSince2000(new DateOnly(2000, 1, 1)));
        Assert.Equal(-1, PostgreSqlBulkWriter.DaysSince2000(new DateOnly(1999, 12, 31)));
        Assert.Equal(-730119, PostgreSqlBulkWriter.DaysSince2000(DateOnly.MinValue));
    }
}
