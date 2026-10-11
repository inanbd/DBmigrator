using System.Data.Common;
using System.Text;

namespace DbMigrator.Desktop.ViewModels;

/// <summary>A failure as the user sees it: one message, plus details for "Copy details".</summary>
public sealed record ErrorInfo(string Message, string Details);

public static class ErrorText
{
    /// <summary>
    /// The message to show for an expected failure. Driver messages are shown verbatim; details
    /// hold the exception chain (types and messages, never a connection string).
    /// </summary>
    public static ErrorInfo From(Exception exception)
    {
        var message = exception switch
        {
            DbException db => db.Message,
            ArgumentException { ParamName: { } field } argument when argument.Message.Contains(field, StringComparison.Ordinal) =>
                $"The connection string is not valid: {argument.Message}",
            ArgumentException argument => $"The connection string is not valid: {argument.Message}",
            CredentialStoreException store => store.Message,
            AggregateException { InnerExceptions.Count: 1 } aggregate => From(aggregate.InnerExceptions[0]).Message,
            _ => exception.Message,
        };
        return new ErrorInfo(message, Details(exception));
    }

    public static string Details(Exception exception)
    {
        var details = new StringBuilder();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (details.Length > 0)
            {
                details.AppendLine().Append("  caused by ");
            }

            details.Append(current.GetType().FullName).Append(": ").Append(current.Message);
            if (current is DbException { SqlState: { } state })
            {
                details.Append(" (SQLSTATE ").Append(state).Append(')');
            }
        }

        return details.ToString();
    }
}
