using Avalonia.Controls;
using Avalonia.Controls.Templates;
using DbMigrator.Desktop.ViewModels;
using DbMigrator.Desktop.Views.Dialogs;
using DbMigrator.Desktop.Views.Parts;
using DbMigrator.Desktop.Views.Steps;

namespace DbMigrator.Desktop;

/// <summary>Finds the view for a view model by its exact type. No reflection, so nothing is missed by trimming.</summary>
internal sealed class ViewLocator : IDataTemplate
{
    private static readonly Dictionary<Type, Func<Control>> Views = new()
    {
        [typeof(SourceStepViewModel)] = () => new SourceStepView(),
        [typeof(TargetStepViewModel)] = () => new TargetStepView(),
        [typeof(TablesStepViewModel)] = () => new TablesStepView(),
        [typeof(ReviewStepViewModel)] = () => new ReviewStepView(),
        [typeof(RunStepViewModel)] = () => new RunStepView(),
        [typeof(ReportStepViewModel)] = () => new ReportStepView(),
        [typeof(ConnectionEditorViewModel)] = () => new ConnectionEditorView(),
        [typeof(TableDetailViewModel)] = () => new TableDetailView(),
        [typeof(DialogHostViewModel)] = () => new DialogHostView(),
    };

    public static bool HasViewFor(Type type) => Views.ContainsKey(type);

    public Control? Build(object? param) =>
        param is not null && Views.TryGetValue(param.GetType(), out var create)
            ? create()
            : new TextBlock { Text = $"No view for {param?.GetType().Name}" };

    public bool Match(object? data) => data is not null && Views.ContainsKey(data.GetType());
}
