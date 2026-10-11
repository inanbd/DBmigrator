using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DbMigrator.Desktop.ViewModels;

/// <summary>The wizard: its steps, the current one, and moving between them.</summary>
public sealed partial class ShellViewModel : ObservableObject, IWizardNavigator, IAsyncDisposable
{
    private bool _navigating;

    public ShellViewModel(ShellServices services)
    {
        Services = services;
        Session = new MigrationSession(services.Providers, services.Time);
        Source = new SourceStepViewModel(services, Session);
        Target = new TargetStepViewModel(services, Session);
        Tables = new TablesStepViewModel(services, Session);
        Review = new ReviewStepViewModel(services, Session);
        Run = new RunStepViewModel(services, Session);
        Report = new ReportStepViewModel(services, Session);
        Steps = [Source, Target, Tables, Review, Run, Report];
        foreach (var step in Steps)
        {
            step.Attach(this);
            step.PropertyChanged += OnStepChanged;
        }

        Current = Source;
        UpdateSteps();
    }

    public ShellServices Services { get; }

    public MigrationSession Session { get; }

    public DialogHostViewModel Dialogs => Services.Dialogs;

    public string Version => Services.Version;

    public string LogFolder => Services.Log.LogFolder;

    public SourceStepViewModel Source { get; }

    public TargetStepViewModel Target { get; }

    public TablesStepViewModel Tables { get; }

    public ReviewStepViewModel Review { get; }

    public RunStepViewModel Run { get; }

    public ReportStepViewModel Report { get; }

    public IReadOnlyList<WizardStepViewModel> Steps { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand), nameof(GoToStepCommand))]
    public partial WizardStepViewModel Current { get; private set; }

    /// <summary>True while a migration runs: the user cannot leave the Run step.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand), nameof(GoToStepCommand))]
    public partial bool IsLocked { get; private set; }

    /// <summary>True while a run is going, so closing the window must ask first.</summary>
    public bool IsRunning => Run.IsRunning;

    public void SetLocked(bool locked) => IsLocked = locked;

    public async Task ReturnToReviewAsync(PreflightResult result)
    {
        Review.ShowPreflightResult(result);
        await GoToAsync(WizardStepKind.Review);
    }

    public async Task RestartAsync(RestartMode mode)
    {
        Review.ResetDecisions();
        Run.Reset();
        Report.Reset();
        Session.ClearRun();
        Session.Prepared = null;
        if (mode == RestartMode.NewMigration)
        {
            Tables.ResetSelection();
        }

        await GoToAsync(mode == RestartMode.RunAgain ? WizardStepKind.Review : WizardStepKind.Tables);
    }

    public async Task GoToAsync(WizardStepKind kind)
    {
        var step = Steps.First(s => s.Kind == kind);
        await EnterAsync(step);
    }

    /// <summary>Asks before closing during a run; stops it and waits briefly for the rollback if confirmed.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!Run.IsRunning)
        {
            return true;
        }

        return await Run.StopForCloseAsync();
    }

    public async ValueTask DisposeAsync() => await Session.DisposeAsync();

    private bool CanGoNext() =>
        !IsLocked && !_navigating && Current.CanAdvance && !Current.IsBusy && (NextApplicable(Current) is not null || Current.NextActsInPlace);

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        await AdvanceAsync();
    }

    private bool CanGoBack() => !IsLocked && !_navigating && !Current.IsBusy && PreviousApplicable(Current) is not null;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task BackAsync()
    {
        if (PreviousApplicable(Current) is { } previous)
        {
            await EnterAsync(previous);
        }
    }

    private bool CanGoTo(WizardStepViewModel? step) =>
        step is not null && !IsLocked && !_navigating && !Current.IsBusy && step != Current && step.State != StepState.Locked;

    /// <summary>Back to any earlier step; forward only through each step in turn, as Next would.</summary>
    [RelayCommand(CanExecute = nameof(CanGoTo))]
    private async Task GoToStepAsync(WizardStepViewModel? step)
    {
        if (step is null)
        {
            return;
        }

        var target = IndexOf(step);
        if (target < IndexOf(Current))
        {
            await EnterAsync(step);
            return;
        }

        while (IndexOf(Current) < target && Current.CanAdvance)
        {
            if (!await AdvanceAsync())
            {
                return;
            }
        }
    }

    private async Task<bool> AdvanceAsync()
    {
        if ((NextApplicable(Current) is null && !Current.NextActsInPlace) || !Current.CanAdvance)
        {
            return false;
        }

        _navigating = true;
        RefreshCommands();
        try
        {
            if (!await Current.OnLeavingAsync(CancellationToken.None))
            {
                return false;
            }
        }
        finally
        {
            _navigating = false;
            RefreshCommands();
        }

        // Leaving can change what applies (e.g. choosing script-only), so look again.
        if (NextApplicable(Current) is not { } after)
        {
            return false;
        }

        await EnterAsync(after);
        return true;
    }

    private async Task EnterAsync(WizardStepViewModel step)
    {
        Current.ClearError();
        Current = step;
        foreach (var each in Steps)
        {
            // Choosing script-only on the Target step hides Run and Report.
            each.RefreshApplicability();
        }

        UpdateSteps();
        await step.OnEnteringAsync(CancellationToken.None);
        UpdateSteps();
    }

    private WizardStepViewModel? NextApplicable(WizardStepViewModel step) =>
        Steps.Skip(IndexOf(step) + 1).FirstOrDefault(s => s.IsApplicable);

    private WizardStepViewModel? PreviousApplicable(WizardStepViewModel step) =>
        Steps.Take(IndexOf(step)).LastOrDefault(s => s.IsApplicable);

    private int IndexOf(WizardStepViewModel step) => Steps.ToList().IndexOf(step);

    private void OnStepChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WizardStepViewModel.CanAdvance) or nameof(WizardStepViewModel.IsBusy) or nameof(WizardStepViewModel.IsApplicable))
        {
            UpdateSteps();
            RefreshCommands();
        }

        if (e.PropertyName == nameof(RunStepViewModel.IsRunning))
        {
            OnPropertyChanged(nameof(IsRunning));
        }
    }

    private void RefreshCommands()
    {
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        GoToStepCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Numbers the applicable steps and works out which can be reached from here.</summary>
    private void UpdateSteps()
    {
        var number = 0;
        var reachable = true;
        var currentIndex = IndexOf(Current);
        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            if (step.IsApplicable)
            {
                step.Number = ++number;
            }

            if (i < currentIndex)
            {
                step.State = StepState.Done;
            }
            else if (i == currentIndex)
            {
                step.State = StepState.Current;
                reachable = step.CanAdvance;
            }
            else
            {
                step.State = reachable && step.IsApplicable ? StepState.Available : StepState.Locked;
                reachable &= step.CanAdvance || !step.IsApplicable;
            }
        }
    }
}
