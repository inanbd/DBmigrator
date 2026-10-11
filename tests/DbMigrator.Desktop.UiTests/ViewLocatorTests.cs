using DbMigrator.Desktop.ViewModels;

namespace DbMigrator.Desktop.UiTests;

public sealed class ViewLocatorTests
{
    [Fact]
    public void Every_step_and_part_has_a_view()
    {
        var needViews = typeof(WizardStepViewModel).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(WizardStepViewModel).IsAssignableFrom(t))
            .Append(typeof(ConnectionEditorViewModel))
            .Append(typeof(TableDetailViewModel))
            .Append(typeof(DialogHostViewModel));

        Assert.All(needViews, t => Assert.True(ViewLocator.HasViewFor(t), $"No view for {t.Name}"));
    }
}
