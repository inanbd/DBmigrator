using System.Globalization;
using DbMigrator.Core.Schema;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbMigrator.Providers.SqlServer;

/// <summary>
/// Turns a default constraint's definition, such as <c>((0))</c>, <c>(N'abc')</c> or
/// <c>(getdate())</c>, into an engine-neutral <see cref="DefaultValue"/>.
/// </summary>
internal static class SqlServerDefaults
{
    public static DefaultValue Parse(string definition)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(definition);
        var expression = parser.ParseExpression(reader, out var errors);
        return (errors.Count == 0 ? Convert(expression) : null) ?? new DefaultValue.Expression(definition);
    }

    private static DefaultValue? Convert(ScalarExpression? expression) => expression switch
    {
        ParenthesisExpression parenthesis => Convert(parenthesis.Expression),
        NullLiteral => new DefaultValue.Literal(null),
        IntegerLiteral integer => Integer(integer.Value),
        NumericLiteral numeric => new DefaultValue.Literal(decimal.Parse(numeric.Value, NumberStyles.Float, CultureInfo.InvariantCulture)),
        MoneyLiteral money => new DefaultValue.Literal(decimal.Parse(money.Value.TrimStart('$'), NumberStyles.Float, CultureInfo.InvariantCulture)),
        RealLiteral real => new DefaultValue.Literal(double.Parse(real.Value, NumberStyles.Float, CultureInfo.InvariantCulture)),
        StringLiteral text => new DefaultValue.Literal(text.Value),
        BinaryLiteral binary => new DefaultValue.Literal(System.Convert.FromHexString(binary.Value[2..])),
        UnaryExpression { UnaryExpressionType: UnaryExpressionType.Positive } unary => Convert(unary.Expression),
        UnaryExpression { UnaryExpressionType: UnaryExpressionType.Negative } unary => Negate(Convert(unary.Expression)),
        ParameterlessCall { ParameterlessCallType: ParameterlessCallType.CurrentTimestamp } =>
            new DefaultValue.Function(DefaultFunction.CurrentDateTime),
        FunctionCall { CallTarget: null, Parameters.Count: 0 } call => Function(call.FunctionName.Value),
        CastCall cast => Typed(cast.DataType, Convert(cast.Parameter)),
        ConvertCall convert => Typed(convert.DataType, Convert(convert.Parameter)),
        _ => null,
    };

    private static DefaultValue.Literal Integer(string digits) =>
        long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? new DefaultValue.Literal(value)
            : new DefaultValue.Literal(decimal.Parse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture));

    private static DefaultValue.Literal? Negate(DefaultValue? value) => value switch
    {
        DefaultValue.Literal { Value: long l } => new DefaultValue.Literal(-l),
        DefaultValue.Literal { Value: decimal d } => new DefaultValue.Literal(-d),
        DefaultValue.Literal { Value: double d } => new DefaultValue.Literal(-d),
        _ => null,
    };

    private static DefaultValue.Function? Function(string name) => name.ToLowerInvariant() switch
    {
        "getdate" or "sysdatetime" => new(DefaultFunction.CurrentDateTime),
        "getutcdate" or "sysutcdatetime" => new(DefaultFunction.CurrentUtcDateTime),
        "sysdatetimeoffset" => new(DefaultFunction.CurrentDateTimeOffset),
        "newid" or "newsequentialid" => new(DefaultFunction.NewGuid),
        _ => null,
    };

    /// <summary>CAST/CONVERT: the column's own type decides how the target renders a value, so only date truncation matters.</summary>
    private static DefaultValue? Typed(DataTypeReference type, DefaultValue? inner)
    {
        var typeName = type.Name.BaseIdentifier.Value.ToLowerInvariant();
        return (typeName, inner) switch
        {
            ("date", DefaultValue.Function { Name: DefaultFunction.CurrentDateTime or DefaultFunction.CurrentDateTimeOffset }) =>
                new DefaultValue.Function(DefaultFunction.CurrentDate),
            ("date", DefaultValue.Function { Name: DefaultFunction.CurrentUtcDateTime }) =>
                new DefaultValue.Function(DefaultFunction.CurrentUtcDate),
            ("date" or "time", DefaultValue.Function) => null,
            _ => inner,
        };
    }
}
