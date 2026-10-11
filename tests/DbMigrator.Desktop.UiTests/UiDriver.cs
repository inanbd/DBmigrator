using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbMigrator.Desktop.Testing;
using DbMigrator.Desktop.ViewModels;
using DbMigrator.Desktop.Views;

namespace DbMigrator.Desktop.UiTests;

/// <summary>Opens the main window over fakes and works it with the mouse and keyboard.</summary>
internal sealed class UiDriver
{
    private UiDriver(TestServices test, WizardDriver wizard, MainWindow window)
    {
        Test = test;
        Wizard = wizard;
        Window = window;
    }

    public TestServices Test { get; }

    public WizardDriver Wizard { get; }

    public ShellViewModel Shell => Wizard.Shell;

    public MainWindow Window { get; }

    public static async Task<UiDriver> OpenAsync(TestServices? test = null)
    {
        test ??= TestServices.Create();
        var wizard = new WizardDriver(test);
        var window = new MainWindow { DataContext = wizard.Shell, Width = 1280, Height = 860 };
        window.Show();
        Settle();
        await wizard.StartAsync();
        Settle();
        return new UiDriver(test, wizard, window);
    }

    /// <summary>Runs queued dispatcher work: bindings, layout and rendering.</summary>
    public static void Settle() => Dispatcher.UIThread.RunJobs();

    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        Settle();
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(10);
            Settle();
        }
    }

    public T Find<T>(string name) where T : Control
    {
        Settle();
        return Find<T>(Window, name);
    }

    public static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible)
        ?? throw new InvalidOperationException($"No visible {typeof(T).Name} named {name}.");

    /// <summary>Whether a control with this name is on screen.</summary>
    public bool IsShown(string name)
    {
        Settle();
        return Window.GetVisualDescendants().OfType<Control>().Any(c => c.Name == name && c.IsEffectivelyVisible);
    }

    /// <summary>A control by its accessible name, as a screen reader would find it.</summary>
    public T ByLabel<T>(string label) where T : Control =>
        Window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => AutomationProperties.GetName(c) == label && c.IsEffectivelyVisible)
        ?? throw new InvalidOperationException($"No visible {typeof(T).Name} labelled {label}.");

    public void Click(Control control)
    {
        Settle();
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)
            ?? throw new InvalidOperationException($"{control.Name} is not in the window.");
        Window.MouseMove(centre, RawInputModifiers.None);
        Window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
        Window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);
        Settle();
    }

    public void Type(TextBox box, string text)
    {
        box.Focus();
        box.SelectAll();
        Settle();
        Window.KeyTextInput(text);
        Settle();
    }

    public void Press(Key key)
    {
        Window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        Window.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Settle();
    }

    /// <summary>Clicks Next and waits for the step change it starts.</summary>
    public async Task NextAsync()
    {
        var from = Shell.Current;
        var next = Find<Button>("NextButton");
        Assert.True(next.IsEffectivelyEnabled, $"Next is disabled on {from.Title}: {from.BlockingReason}");
        Click(next);
        await UntilAsync(() => Shell.Current != from || !Shell.NextCommand.IsRunning, "Next to finish");
    }

    /// <summary>Saves what the window shows, for the CI artifact and for looking at by eye.</summary>
    public void Screenshot(string name)
    {
        Settle();
        using var frame = Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        var folder = Environment.GetEnvironmentVariable("DBMIGRATOR_SCREENSHOTS") is { Length: > 0 } custom
            ? custom
            : Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(folder);
        frame.Save(Path.Combine(folder, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}
