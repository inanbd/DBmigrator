using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Testing;

/// <summary>Drives the wizard the way a user would: fill in a form, connect, press Next.</summary>
public sealed class WizardDriver(TestServices test)
{
    public const string Password = "s3cret-Pa55";

    public TestServices Test => test;

    public ShellViewModel Shell { get; } = new(test.Services);

    public static void Fill(ConnectionEditorViewModel editor, string server, string database, string? user = "migrator", string? password = Password)
    {
        Field(editor, "Data Source").Value = server;
        Field(editor, "Initial Catalog").Value = database;
        Field(editor, "User ID").Value = user ?? "";
        Field(editor, "Password").Value = password ?? "";
    }

    public static ConnectionFieldViewModel Field(ConnectionEditorViewModel editor, string keyword) =>
        editor.AllFields.First(f => string.Equals(f.Keyword, keyword, StringComparison.OrdinalIgnoreCase));

    /// <summary>Enters the first step, as the window does when it opens.</summary>
    public Task StartAsync() => Shell.StartAsync();

    public async Task ConnectSourceAsync()
    {
        Fill(Shell.Source.Editor!, "src01", "shop");
        await Shell.Source.ConnectCommand.ExecuteAsync(null);
        Check(Shell.Source.IsConnected, $"source did not connect: {Shell.Source.Error}");
    }

    public async Task ConnectTargetAsync()
    {
        Fill(Shell.Target.Editor!, "pg01", "shop");
        await Shell.Target.ConnectCommand.ExecuteAsync(null);
        Check(Shell.Target.IsConnected, $"target did not connect: {Shell.Target.Error}");
    }

    public async Task NextAsync()
    {
        var from = Shell.Current;
        Check(Shell.NextCommand.CanExecute(null), $"Next is disabled on {from.Title}: {from.BlockingReason}");
        await Shell.NextCommand.ExecuteAsync(null);
    }

    /// <summary>Source and target connected, all tables selected, standing on Review.</summary>
    public async Task ToReviewAsync()
    {
        await StartAsync();
        await ConnectSourceAsync();
        await NextAsync();
        await ConnectTargetAsync();
        await NextAsync();
        await NextAsync();
        Check(Shell.Current == Shell.Review, $"expected Review, on {Shell.Current.Title}");
    }

    /// <summary>On Review with every table decided, then on to Run.</summary>
    public async Task ToRunAsync()
    {
        await ToReviewAsync();
        await NextAsync();
        Check(Shell.Current == Shell.Run, $"expected Run, on {Shell.Current.Title}: {Shell.Review.BlockingReason}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
