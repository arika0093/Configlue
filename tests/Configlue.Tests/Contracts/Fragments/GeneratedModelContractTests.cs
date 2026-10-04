using Configlue.CompilerServices;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class GeneratedModelContractTests
{
    [Test]
    public void CompilerPlumbingIsHiddenFromTheApplicationModelAndBuilder()
    {
        typeof(AppSettings).GetMethod("CreateConfiglueRuntime").ShouldBeNull();
        typeof(AppSettings).GetMethod("ToFragment").ShouldBeNull();
        typeof(ConfiglueModelBuilder<AppSettings>).GetMethod("BuildSources").ShouldBeNull();
        typeof(ConfiglueModelBuilder<AppSettings>).GetMethod("GetMigrations").ShouldBeNull();
        typeof(IConfiglueDiagnostics<AppSettings>)
            .GetMethod("GetDetailsSnapshotAsync")
            .ShouldBeNull();
        typeof(IConfiglueFacadeModel<AppSettings>)
            .GetProperties()
            .Select(property => property.Name)
            .ShouldBe([]);
    }

    [Test]
    public void IConfiglueModel_ConvertsModelsAndCreatesSparseSemanticDiffs()
    {
        var before = new AppSettings { RetryCount = 3, Label = "before" };
        var after = new AppSettings { RetryCount = 3, Label = "after" };

        var result = ExerciseModelBridge<AppSettings, AppSettings.Fragment>(before, after);

        result.Schema.ShouldBe(AppSettings.ConfiglueSchema);
        result.Complete.RetryCount.ShouldBe(Optional<int>.Present(3));
        result.Complete.Label.ShouldBe(Optional<string?>.Present("before"));
        result.Changes.RetryCount.IsPresent.ShouldBeFalse();
        result.Changes.Label.ShouldBe(Optional<string?>.Present("after"));
        result.Restored.RetryCount.ShouldBe(3);
        result.Restored.Label.ShouldBe("before");
    }

    [Test]
    public async Task IConfiglueFacadeModel_ProvidesSchemaAndCreatesItsTypedRuntime()
    {
        var schema = GetFacadeSchema<AppSettings>();
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(13) }
        );
        var configuration = new ConfiglueModelBuilder<AppSettings>();
        configuration.Sources(sources =>
            sources.Add(new StateSource<AppSettings.Fragment>("interface-source", store, new StateSourceOptions<AppSettings.Fragment>()))
        );
        var options = CreateRuntime<AppSettings>(configuration, static _ => { });
        try
        {
            schema.ShouldBe(AppSettings.ConfiglueSchema);
            schema.Id.ShouldBe("app-settings");
            (await options.GetValueAsync()).RetryCount.ShouldBe(13);
        }
        finally
        {
            if (options is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
        }
    }

    [Test]
    public void IConfiglueFragment_AppliesChangesAndSupportsUntypedMemberEditing()
    {
        IConfiglueFragment<AppSettings.Fragment> lower = new AppSettings.Fragment
        {
            Plugins = Optional<IReadOnlyList<string>>.Present(["base"]),
            RetryCount = Optional<int>.Present(3),
        };
        var higher = new AppSettings.Fragment
        {
            Plugins = Optional<IReadOnlyList<string>>.Present(["custom"]),
        };

        var applied = lower.ApplyChanges(higher);
        applied.Plugins.Value.ShouldBe(["custom"]);
        lower.Merge(higher).Plugins.Value.ShouldBe(["base", "custom"]);

        IConfiglueFragment untyped = applied;
        var retryCountId = AppSettings
            .FragmentSchema.Members.Single(static member => member.Name == "RetryCount")
            .Id;
        var changed = (AppSettings.Fragment)untyped.WithMember(retryCountId, 9);
        var removed = (AppSettings.Fragment)changed.WithoutMember(retryCountId);

        changed.RetryCount.ShouldBe(Optional<int>.Present(9));
        removed.RetryCount.IsPresent.ShouldBeFalse();
        untyped
            .EnumeratePresentMembers()
            .Select(static member => member.Name)
            .ShouldContain("Plugins");
    }

    private static (
        ConfiglueModelSchema Schema,
        TFragment Complete,
        TFragment Changes,
        TModel Restored
    ) ExerciseModelBridge<TModel, TFragment>(TModel before, TModel after)
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        var operations = ConfiglueModelOperations<TModel, TFragment>.Current;
        var complete = operations.ToFragment(before);
        return (
            operations.Schema,
            complete,
            operations.Diff(before, after),
            operations.FromFragment(complete)
        );
    }

    private static ConfiglueModelSchema GetFacadeSchema<TModel>()
        where TModel : IConfiglueFacadeModel<TModel> =>
        ConfiglueModelDescriptor<TModel>.Current.Schema;

    private static IWritableState<TModel> CreateRuntime<TModel>(
        ConfiglueModelBuilder<TModel> configuration,
        Action<object> ownResource
    )
        where TModel : IConfiglueFacadeModel<TModel> =>
        ConfiglueModelDescriptor<TModel>.Current.CreateRuntime(configuration, null, ownResource);
}
