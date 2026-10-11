using System.Data.Common;
using System.Net.Sockets;

namespace DbMigrator.Desktop.ViewModels;

/// <summary>Suggestions for common connection failures. Only recognised cases get one; guesses would mislead.</summary>
public static class ConnectionHints
{
    public static IReadOnlyList<string> For(Exception exception)
    {
        var hints = new List<string>();
        var messages = string.Join(" | ", Chain(exception).Select(e => e.Message));
        var state = Chain(exception).OfType<DbException>().Select(e => e.SqlState).FirstOrDefault(s => s is not null);

        if (Contains(messages, "Login failed") || state is "28P01" or "28000" || Contains(messages, "password authentication failed"))
        {
            hints.Add("Check the user and password. On SQL Server, also check that SQL Server authentication is enabled.");
        }

        if (state == "3D000" || Contains(messages, "Cannot open database") || (Contains(messages, "database") && Contains(messages, "does not exist")))
        {
            hints.Add("Check the database name, and that this login may connect to it.");
        }

        if (Contains(messages, "certificate") && (Contains(messages, "chain") || Contains(messages, "trust") || Contains(messages, "validation")))
        {
            hints.Add("Install the server's CA certificate on this computer, or turn on \"Trust server certificate\" on a network you trust.");
        }

        if (Chain(exception).Any(e => e is SocketException or TimeoutException)
            || Contains(messages, "network-related") || Contains(messages, "No such host") || Contains(messages, "timeout")
            || Contains(messages, "Connection refused"))
        {
            hints.Add("Check the server name and port, and that a firewall allows the connection.");
        }

        return hints;
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    private static bool Contains(string text, string fragment) => text.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
