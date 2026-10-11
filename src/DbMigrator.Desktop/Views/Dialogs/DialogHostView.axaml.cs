using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DbMigrator.Desktop.Views.Dialogs;

internal sealed partial class DialogHostView : UserControl
{
    public DialogHostView()
    {
        InitializeComponent();

        // Keyboard focus moves into a dialog when it opens, so Enter and Esc reach its buttons.
        DialogContent.PropertyChanged += (_, e) =>
        {
            if (e.Property == ContentControl.ContentProperty && e.NewValue is not null)
            {
                Dispatcher.UIThread.Post(FocusDialog, DispatcherPriority.Loaded);
            }
        };
    }

    private void FocusDialog()
    {
        var target = DialogContent.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()
            ?? (Control?)DialogContent.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsDefault);
        target?.Focus();
    }
}
