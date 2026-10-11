using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Views;

internal sealed partial class MainWindow : Window
{
    private bool _closeConfirmed;
    private bool _askingToClose;

    public MainWindow()
    {
        InitializeComponent();
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    /// <summary>
    /// Closing during a run asks first. The veto is set before the first await; once confirmed,
    /// the run is stopped, its rollback awaited for a while, and the window closes for real.
    /// </summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || Shell is not { IsRunning: true } shell)
        {
            return;
        }

        if (e.CloseReason == WindowCloseReason.OSShutdown)
        {
            // The system is going down: no question. The server rolls back the copy when the connection drops.
            shell.Run.RunCancelCommand.Execute(null);
            return;
        }

        e.Cancel = true;
        if (_askingToClose)
        {
            return;
        }

        _askingToClose = true;
        try
        {
            if (await shell.ConfirmCloseAsync())
            {
                _closeConfirmed = true;
                Close();
            }
        }
        finally
        {
            _askingToClose = false;
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (Shell is { } shell)
        {
            await shell.DisposeAsync();
        }
    }

    private async void OnOpenLogFolder(object? sender, RoutedEventArgs e)
    {
        if (Shell is { } shell && Directory.Exists(shell.LogFolder))
        {
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(shell.LogFolder));
        }
    }
}
