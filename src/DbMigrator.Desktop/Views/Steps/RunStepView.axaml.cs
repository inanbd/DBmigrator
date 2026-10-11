using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.Views.Steps;

internal sealed partial class RunStepView : UserControl
{
    private RunStepViewModel? _viewModel;

    public RunStepView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelChanged;
            }

            _viewModel = DataContext as RunStepViewModel;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelChanged;
            }
        };
    }

    /// <summary>Keeps the table being worked on in view while "Follow current table" is ticked.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RunStepViewModel.CurrentRow) && _viewModel is { FollowCurrent: true, CurrentRow: { } row })
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (RunTablesGrid.ItemsSource is IEnumerable<TableProgressRowViewModel> rows && rows.Contains(row))
                {
                    RunTablesGrid.ScrollIntoView(row, RunTablesGrid.Columns[0]);
                }
            }, DispatcherPriority.Background);
        }
    }
}
