using CommunityToolkit.Mvvm.ComponentModel;

namespace DbMigrator.Desktop.ViewModels;

public enum WizardStepKind
{
    Source,
    Target,
    Tables,
    Review,
    // Milestone 4 inserts a stored-code review step here; it is skipped when not applicable.
    Run,
    Report,
}

/// <summary>A step's place in the rail. Each is shown with a word as well as a mark, never colour alone.</summary>
public enum StepState
{
    Locked,
    Available,
    Current,
    Done,
}

/// <summary>How the Report step starts over.</summary>
public enum RestartMode
{
    /// <summary>Same tables and options: plan again and ask about existing tables again.</summary>
    RunAgain,

    /// <summary>Keep the connections; choose tables afresh.</summary>
    NewMigration,
}

/// <summary>What steps may ask of the shell.</summary>
public interface IWizardNavigator
{
    Task GoToAsync(WizardStepKind kind);

    /// <summary>While locked (a run is going) the user cannot leave the current step.</summary>
    void SetLocked(bool locked);

    /// <summary>The target changed between review and start: show why on the Review step.</summary>
    Task ReturnToReviewAsync(PreflightResult result);

    /// <summary>Clears the last run and its decisions, then goes to the step the mode starts from.</summary>
    Task RestartAsync(RestartMode mode);
}

/// <summary>One step of the wizard: its title, whether the user may move on, and what it does on the way in and out.</summary>
public abstract partial class WizardStepViewModel : ObservableObject
{
    private IWizardNavigator? _navigator;

    public abstract WizardStepKind Kind { get; }

    public abstract string Title { get; }

    public abstract string Subtitle { get; }

    public virtual string NextLabel => "Next";

    /// <summary>Next does something on this step instead of moving on, e.g. saving the script when there is no target.</summary>
    public virtual bool NextActsInPlace => false;

    /// <summary>The step's number in the rail, counting applicable steps only.</summary>
    [ObservableProperty]
    public partial int Number { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial StepState State { get; set; }

    public string StateText => State switch
    {
        StepState.Done => "Done",
        StepState.Current => "Current",
        StepState.Available => "Available",
        _ => "Locked",
    };

    [ObservableProperty]
    public partial bool IsBusy { get; protected set; }

    [ObservableProperty]
    public partial string? BusyText { get; protected set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; protected set; }

    [ObservableProperty]
    public partial string? ErrorDetails { get; protected set; }

    public bool HasError => Error is not null;

    /// <summary>Whether Next is allowed. Steps raise a change notification when its inputs change.</summary>
    public abstract bool CanAdvance { get; }

    /// <summary>Why Next is not allowed, in words, e.g. "Connect to the source to continue".</summary>
    public abstract string? BlockingReason { get; }

    /// <summary>Steps that do not apply (e.g. Run when only a script is wanted) are skipped and hidden.</summary>
    public virtual bool IsApplicable => true;

    public virtual bool ShowsFooter => true;

    protected IWizardNavigator Navigator => _navigator ?? throw new InvalidOperationException("The step is not attached to a shell.");

    internal void Attach(IWizardNavigator navigator) => _navigator = navigator;

    /// <summary>Called when the step becomes current; rebuilds what changed since it was last shown.</summary>
    public virtual Task OnEnteringAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Called before moving on with Next; returning false stays on the step.</summary>
    public virtual Task<bool> OnLeavingAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    /// <summary>Tells bindings that <see cref="IsApplicable"/> may have changed.</summary>
    public void RefreshApplicability() => OnPropertyChanged(nameof(IsApplicable));

    public void ClearError()
    {
        Error = null;
        ErrorDetails = null;
    }

    protected void RaiseCanAdvanceChanged()
    {
        OnPropertyChanged(nameof(CanAdvance));
        OnPropertyChanged(nameof(BlockingReason));
    }

    protected virtual void ShowError(Exception exception)
    {
        var info = ErrorText.From(exception);
        Error = info.Message;
        ErrorDetails = info.Details;
    }

    /// <summary>Runs work with the step busy; expected failures become <see cref="Error"/>, a cancel is silent.</summary>
    protected async Task<bool> GuardAsync(Func<CancellationToken, Task> work, string busyText, CancellationToken cancellationToken)
    {
        IsBusy = true;
        BusyText = busyText;
        ClearError();
        RaiseCanAdvanceChanged();
        try
        {
            await work(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowError(ex);
            return false;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
            RaiseCanAdvanceChanged();
        }
    }
}
