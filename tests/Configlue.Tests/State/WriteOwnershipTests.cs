using Configlue.Extensibility;
using Configlue.Testing;
using Configlue.Sources;

namespace Configlue.Tests;

public sealed class WriteOwnershipTests
{
    [Test]
    public async Task SingleNonExplicitWritableRootSourceIsInferred()
    {
        var user = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var policy = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("policy") }
        );
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("policy", policy, new StateSourceOptions<AppSettings.Fragment> { Priority = 100 }),
                new StateSource<AppSettings.Fragment>("user", user, new StateSourceOptions<AppSettings.Fragment> { Priority = 0, Writer = user }),
            ])
        );

        var diagnostics = options.GetDiagnostics();
        diagnostics.DefaultWriteSourceId.ShouldBe(SourceId.From("user"));
        diagnostics.DefaultWriteSourceIsInferred.ShouldBeTrue();

        await options.SaveAsync(settings => settings.RetryCount = 9);

        (await user.ReadAsync()).Value!.RetryCount.Value.ShouldBe(9);
    }

    [Test]
    public void MultipleWritableRootSourcesWithoutADefaultFailContextCreation()
    {
        var first = new InMemoryStateSource<AppSettings.Fragment>();
        var second = new InMemoryStateSource<AppSettings.Fragment>();

        Should.Throw<InvalidOperationException>(() =>
            ConfiglueApp.CreateContext(builder =>
                builder.Add<AppSettings>(model =>
                    model.Sources(sources =>
                    {
                        sources.Add(
                            new StateSource<AppSettings.Fragment>("first", first, new StateSourceOptions<AppSettings.Fragment> { Writer = first })
                        );
                        sources.Add(
                            new StateSource<AppSettings.Fragment>("second", second, new StateSourceOptions<AppSettings.Fragment> { Writer = second })
                        );
                    })
                )
            )
        );
    }

    [Test]
    public async Task ExplicitDefaultOwnerSelectsTheOrdinaryWriteTarget()
    {
        var first = new InMemoryStateSource<AppSettings.Fragment>();
        var second = new InMemoryStateSource<AppSettings.Fragment>();
        await using var context = ConfiglueApp.CreateContext(builder =>
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                {
                    sources.Add(
                        new StateSource<AppSettings.Fragment>("first", first, new StateSourceOptions<AppSettings.Fragment> { Writer = first })
                    );
                    sources.Add(
                        new StateSource<AppSettings.Fragment>("second", second, new StateSourceOptions<AppSettings.Fragment> { Writer = second })
                    );
                });
                model.Writes(write => write.DefaultTo(SourceKey<AppSettings>.Named("second")));
            })
        );

        var options = context.GetRuntimeState<AppSettings>();
        var diagnostics = options.GetDiagnostics();
        diagnostics.DefaultWriteSourceId.ShouldBe(SourceId.From("second"));
        diagnostics.DefaultWriteSourceIsInferred.ShouldBeFalse();

        await options.SaveAsync(settings => settings.RetryCount = 7);

        (await second.ReadAsync()).Value!.RetryCount.Value.ShouldBe(7);
        (await first.ReadAsync()).Status.ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public async Task ExplicitPropertyRouteOwnsThatPathOverTheDefaultOwner()
    {
        var root = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var database = new InMemoryStateSource<AppSettings.Fragment>();
        await using var context = ConfiglueApp.CreateContext(builder =>
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                {
                    sources.Add(
                        new StateSource<AppSettings.Fragment>("root", root, new StateSourceOptions<AppSettings.Fragment> { Writer = root })
                    );
                    sources.Add(
                        new StateSource<AppSettings.Fragment>("database", database, new StateSourceOptions<AppSettings.Fragment> { Writer = database })
                    );
                });
                model.Writes(write =>
                {
                    write.DefaultTo(SourceKey<AppSettings>.Named("root"));
                    write.Route(x => x.Database, SourceKey<AppSettings>.Named("database"));
                });
            })
        );

        var options = context.GetRuntimeState<AppSettings>();
        await options.SaveAsync(settings =>
        {
            settings.RetryCount = 9;
            settings.Database!.Host = "routed.db";
        });

        (await root.ReadAsync()).Value!.RetryCount.Value.ShouldBe(9);
        (await root.ReadAsync()).Value!.Database.IsPresent.ShouldBeFalse();
        (await database.ReadAsync()).Value!.Database.Value!.Host.Value.ShouldBe("routed.db");
        (await database.ReadAsync()).Value!.RetryCount.IsPresent.ShouldBeFalse();
    }

    [Test]
    public async Task WritableMountedSourceOwnsItsMountedSubtree()
    {
        var rootStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var databaseStore = new InMemoryStateSource<DatabaseSettings.Fragment>(
            new DatabaseSettings.Fragment { Host = Optional<string>.Present("remote.db") }
        );
        await using var context = ConfiglueApp.CreateContext(builder =>
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.Add(
                        new StateSource<AppSettings.Fragment>("root", rootStore, new StateSourceOptions<AppSettings.Fragment> { Writer = rootStore })
                    );
                    sources.AddMounted<
                        AppSettings,
                        AppSettings.Fragment,
                        DatabaseSettings,
                        DatabaseSettings.Fragment
                    >(
                        new StateSource<DatabaseSettings.Fragment>("database-owner", databaseStore, new StateSourceOptions<DatabaseSettings.Fragment> { Writer = databaseStore }),
                        settings => settings.Database
                    );
                })
            )
        );

        var options = context.GetRuntimeState<AppSettings>();
        await options.SaveAsync(settings => settings.Database!.Host = "mounted.db");
        await options.SaveAsync(settings => settings.RetryCount = 11);

        (await databaseStore.ReadAsync()).Value!.Host.Value.ShouldBe("mounted.db");
        (await rootStore.ReadAsync()).Value!.RetryCount.Value.ShouldBe(11);
        (await rootStore.ReadAsync()).Value!.Database.IsPresent.ShouldBeFalse();
    }

    [Test]
    public async Task ExplicitOnlySourceIsExcludedFromOrdinaryOwnershipInference()
    {
        var ordinary = new InMemoryStateSource<AppSettings.Fragment>();
        var explicitOnly = new InMemoryStateSource<AppSettings.Fragment>();
        await using var context = ConfiglueApp.CreateContext(builder =>
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.Add(
                        new StateSource<AppSettings.Fragment>("ordinary", ordinary, new StateSourceOptions<AppSettings.Fragment> { Writer = ordinary })
                    );
                    sources.Add(
                        new StateSource<AppSettings.Fragment>("explicit", explicitOnly, new StateSourceOptions<AppSettings.Fragment> { Writer = explicitOnly, ExplicitOnly = true })
                    );
                })
            )
        );

        var options = context.GetRuntimeState<AppSettings>();
        var diagnostics = options.GetDiagnostics();
        diagnostics.DefaultWriteSourceId.ShouldBe(SourceId.From("ordinary"));
        diagnostics.DefaultWriteSourceIsInferred.ShouldBeTrue();

        await options.SaveAsync(settings => settings.RetryCount = 4);
        (await ordinary.ReadAsync()).Value!.RetryCount.Value.ShouldBe(4);
        (await explicitOnly.ReadAsync()).Status.ShouldBe(StateReadStatus.NotFound);

        var explicitPatch = new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(6) };
        await options.Source(SourceKey<AppSettings>.Named("explicit")).SaveAsync(explicitPatch);
        (await explicitOnly.ReadAsync()).Value!.RetryCount.Value.ShouldBe(6);
    }

    [Test]
    public async Task HigherPriorityReadOnlyContributionSurfacesAsAConflictInsteadOfRerouting()
    {
        var policy = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Enabled = Optional<bool>.Present(true) }
        );
        var user = new InMemoryStateSource<AppSettings.Fragment>();
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("policy", policy, new StateSourceOptions<AppSettings.Fragment> { Priority = 100 }),
                new StateSource<AppSettings.Fragment>("user", user, new StateSourceOptions<AppSettings.Fragment> { Priority = 0, Writer = user }),
            ]),
            StateWritePlan.DefaultTo(SourceId.From("user"))
        );

        var rejection = await Should.ThrowAsync<StateConflictException>(async () =>
            await options.SaveAsync(
                new AppSettings.Patch { Enabled = FragmentOperation<bool>.Set(false) }
            )
        );

        rejection.Message.ShouldContain("policy");
        (await user.ReadAsync()).Status.ShouldBe(StateReadStatus.NotFound);
    }
}
