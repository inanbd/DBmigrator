using Avalonia;

namespace DbMigrator.Desktop;

internal static class Program
{
    /// <summary>Exit code when there is no screen to show the window on.</summary>
    public const int NoDisplay = 2;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex) when (OperatingSystem.IsLinux() && ex.Message.Contains("XOpenDisplay", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("No graphical display was found. On a server, use the dbmigrator command line instead.");
            return NoDisplay;
        }
    }

    /// <summary>Also used by the visual designer.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
