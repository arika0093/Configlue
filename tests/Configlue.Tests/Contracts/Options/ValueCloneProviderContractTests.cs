using Configlue.Testing;

namespace Configlue.Tests;

public sealed class ValueCloneProviderContractTests
{
    [Test]
    public void CloneValue_ProducesIndependentModelAndNestedMutableValues()
    {
        using var options = CreateOptions();
        IConfiglueValueCloneProvider<AppSettings> cloneProvider = options;
        var plugins = new List<string> { "initial" };
        var original = new AppSettings
        {
            Database = new DatabaseSettings { Host = "original.db" },
            Plugins = plugins,
        };

        var clone = cloneProvider.CloneValue(original);
        plugins.Add("original-only");
        clone.Database!.Host = "clone.db";

        clone.ShouldNotBeSameAs(original);
        clone.Database.ShouldNotBeSameAs(original.Database);
        clone.Plugins.SequenceEqual(["initial"]).ShouldBeTrue();
        original.Database!.Host.ShouldBe("original.db");
    }

    [Test]
    public void CloneValue_RejectsACloneStrategyThatReturnsTheOriginalInstance()
    {
        using var options = CreateOptions(static value => value);
        IConfiglueValueCloneProvider<AppSettings> cloneProvider = options;

        Should.Throw<InvalidOperationException>(() => cloneProvider.CloneValue(new AppSettings()));
    }

    private static ConfiglueRuntime<AppSettings, AppSettings.Fragment> CreateOptions(
        Func<AppSettings, AppSettings>? cloneStrategy = null
    ) =>
        new(
            new StateSourceSet<AppSettings.Fragment>([
                new("defaults", new InMemoryStateSource<AppSettings.Fragment>()),
            ]),
            writeRoute: default,
            defaultWritePlan: StateWritePlan.Empty,
            migrations: null,
            validators: null,
            validateDataAnnotations: false,
            onChangeDebounce: null,
            stateName: null,
            logger: null,
            cloneStrategy: cloneStrategy
        );
}
