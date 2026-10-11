using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DbMigrator.Desktop.ViewModels;

/// <summary>The dialog currently shown over the window.</summary>
public abstract class DialogViewModel : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>Answers the dialog as if cancelled (Esc, or another dialog replacing it).</summary>
    public abstract void Dismiss();
}

public sealed partial class ConfirmDialogViewModel(ConfirmRequest request, Action<bool> answer) : DialogViewModel
{
    public override string Title => request.Title;

    public string Message => request.Message;

    public IReadOnlyList<string> Bullets => request.Bullets;

    public bool HasBullets => request.Bullets.Count > 0;

    public string ConfirmText => request.ConfirmText;

    public string CancelText => request.CancelText;

    public bool IsDestructive => request.IsDestructive;

    [RelayCommand]
    private void Confirm() => answer(true);

    [RelayCommand]
    private void Cancel() => answer(false);

    public override void Dismiss() => answer(false);
}

public sealed partial class PromptDialogViewModel(PromptRequest request, Action<string?> answer) : DialogViewModel
{
    public override string Title => request.Title;

    public string Label => request.Label;

    public string ConfirmText => request.ConfirmText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string Text { get; set; } = request.InitialValue;

    private bool CanConfirm => !string.IsNullOrWhiteSpace(Text);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => answer(Text.Trim());

    [RelayCommand]
    private void Cancel() => answer(null);

    public override void Dismiss() => answer(null);
}

public sealed partial class ErrorDialogViewModel(ErrorRequest request, Action answer) : DialogViewModel
{
    public override string Title => request.Title;

    public string Message => request.Message;

    public string? Details => request.Details;

    public bool HasDetails => !string.IsNullOrEmpty(request.Details);

    [RelayCommand]
    private void Close() => answer();

    public override void Dismiss() => answer();
}

/// <summary>
/// Shows dialogs as an overlay inside the main window, one at a time. A new request answers the
/// one showing as cancelled. Answers complete asynchronously, so callers never resume inside
/// the click handler.
/// </summary>
public sealed partial class DialogHostViewModel : ObservableObject, IDialogService
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    public partial DialogViewModel? Current { get; private set; }

    public bool IsOpen => Current is not null;

    public Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Show(new ConfirmDialogViewModel(request, confirmed => Close(answer, confirmed)));
        return answer.Task;
    }

    public Task<string?> PromptAsync(PromptRequest request)
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Show(new PromptDialogViewModel(request, text => Close(answer, text)));
        return answer.Task;
    }

    public Task ShowErrorAsync(ErrorRequest request)
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Show(new ErrorDialogViewModel(request, () =>
        {
            Current = null;
            answer.TrySetResult();
        }));
        return answer.Task;
    }

    /// <summary>Answers the open dialog as cancelled, e.g. on Esc.</summary>
    [RelayCommand]
    private void Dismiss() => Current?.Dismiss();

    private void Show(DialogViewModel dialog)
    {
        Current?.Dismiss();
        Current = dialog;
    }

    private void Close<T>(TaskCompletionSource<T> answer, T value)
    {
        Current = null;
        answer.TrySetResult(value);
    }
}
