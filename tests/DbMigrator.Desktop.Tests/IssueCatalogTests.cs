using System.Reflection;
using DbMigrator.Core.Diagnostics;
using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Tests;

public sealed class IssueCatalogTests
{
    public static TheoryData<string> Codes()
    {
        var codes = typeof(IssueCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);
        return [.. codes];
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Every_issue_code_is_explained(string code)
    {
        var help = IssueCatalog.For(code);

        Assert.False(string.IsNullOrWhiteSpace(help.Title));
        Assert.False(string.IsNullOrWhiteSpace(help.Meaning));
        Assert.False(string.IsNullOrWhiteSpace(help.Action));
        Assert.NotEqual(IssueCatalog.For("no-such-code-ever").Title, help.Title);
    }
}
