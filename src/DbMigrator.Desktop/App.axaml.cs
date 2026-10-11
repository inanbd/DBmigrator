using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DbMigrator.Desktop.ViewModels;
using DbMigrator.Desktop.Views;

namespace DbMigrator.Desktop;

internal sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Under the headless test session there is no lifetime: tests build their own window.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var shell = CompositionRoot.CreateShell(window);
            window.DataContext = shell;
            desktop.MainWindow = window;
            InstallExceptionHooks(shell);
            window.Opened += async (_, _) => await shell.StartAsync();

            // Used by CI under Xvfb: the app starts, shows its window, and exits cleanly.
            if (desktop.Args?.Contains("--smoke-test") == true)
            {
                window.Opened += (_, _) => DispatcherTimer.RunOnce(() => desktop.Shutdown(0), TimeSpan.FromSeconds(2), DispatcherPriority.Background);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void InstallExceptionHooks(ShellViewModel shell)
    {
        var log = shell.Services.Log;
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            log.Error("Unexpected error on the UI thread.", e.Exception);
            var info = ErrorText.From(e.Exception);
            _ = shell.Dialogs.ShowErrorAsync(new ErrorRequest(
                "Something went wrong",
                $"{info.Message} Details are in the log: {log.LogFolder}",
                info.Details));
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Error("Unexpected error.", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Error("Unobserved task error.", e.Exception);
            e.SetObserved();
        };
    }
}
