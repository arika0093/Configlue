using Configlue.Testing;

namespace Configlue.Tests;

public sealed class CompositeWriteOwnershipTests
{
    [Test]
    public async Task RouteOnlyComposite_SavesRoutedMemberToChild()
    {
        var childStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "child",
                    childStore,
                    writer: childStore,
                    watcher: childStore
                ),
            ]),
            writePlan: new StateWritePlan(
                null,
                new Dictionary<string, SourceId>(StringComparer.Ordinal)
                {
                    ["RetryCount"] = SourceId.From("child"),
                }
            )
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.Add(composite.CreateSource("combined")))
            );
        });

        var state = context.GetRuntimeState<AppSettings>();
        using var edit = await state.OpenEditSessionAsync();
        edit.Value.RetryCount = 2;
        await edit.CommitAsync();

        (await childStore.ReadAsync()).Value!.RetryCount.Value.ShouldBe(2);
    }

    [Test]
    public async Task PlanDefaultOnlyComposite_IsRecognizedAsWritableAndSaves()
    {
        var childStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "child",
                    childStore,
                    writer: childStore,
                    watcher: childStore
                ),
            ]),
            writePlan: StateWritePlan.DefaultTo(SourceId.From("child"))
        );

        composite.DefaultWriteSourceId.ShouldBe(SourceId.From("child"));
        composite.HasWriteRoutes.ShouldBeTrue();
        composite.CreateSource("combined").Writer.ShouldNotBeNull();

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.Add(composite.CreateSource("combined")))
            );
        });

        var state = context.GetRuntimeState<AppSettings>();
        using var edit = await state.OpenEditSessionAsync();
        edit.Value.RetryCount = 2;
        await edit.CommitAsync();

        (await childStore.ReadAsync()).Value!.RetryCount.Value.ShouldBe(2);
    }

    [Test]
    public async Task ExplicitDefaultComposite_KeepsWorking()
    {
        var childStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "child",
                    childStore,
                    writer: childStore,
                    watcher: childStore
                ),
            ]),
            SourceId.From("child")
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.Add(composite.CreateSource("combined")))
            );
        });

        var state = context.GetRuntimeState<AppSettings>();
        using var edit = await state.OpenEditSessionAsync();
        edit.Value.RetryCount = 2;
        await edit.CommitAsync();

        (await childStore.ReadAsync()).Value!.RetryCount.Value.ShouldBe(2);
    }

    [Test]
    public void ExplicitDefault_WinsOverPlanDefault()
    {
        var firstStore = new InMemoryStateSource<AppSettings.Fragment>();
        var secondStore = new InMemoryStateSource<AppSettings.Fragment>();
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "first",
                    firstStore,
                    writer: firstStore,
                    watcher: firstStore
                ),
                new StateSource<AppSettings.Fragment>(
                    "second",
                    secondStore,
                    writer: secondStore,
                    watcher: secondStore
                ),
            ]),
            SourceId.From("first"),
            StateWritePlan.DefaultTo(SourceId.From("second"))
        );

        composite.DefaultWriteSourceId.ShouldBe(SourceId.From("first"));
        composite.ResolveWriteComponent("RetryCount").Id.ShouldBe(SourceId.From("first"));
        composite.CreateSource("combined").Writer.ShouldNotBeNull();
    }

    [Test]
    public void UnknownDefaultComponent_IsRejected()
    {
        var childStore = new InMemoryStateSource<AppSettings.Fragment>();
        StateSourceSet<AppSettings.Fragment> Components() =>
            new([
                new StateSource<AppSettings.Fragment>(
                    "child",
                    childStore,
                    writer: childStore,
                    watcher: childStore
                ),
            ]);

        Should.Throw<ArgumentException>(() =>
            new CompositeStateSource<AppSettings.Fragment>(
                Components(),
                SourceId.From("missing")
            )
        );
        Should.Throw<ArgumentException>(() =>
            new CompositeStateSource<AppSettings.Fragment>(
                Components(),
                writePlan: StateWritePlan.DefaultTo(SourceId.From("missing"))
            )
        );
        Should.Throw<ArgumentException>(() =>
            new CompositeStateSource<AppSettings.Fragment>(
                Components(),
                writePlan: new StateWritePlan(
                    null,
                    new Dictionary<string, SourceId>(StringComparer.Ordinal)
                    {
                        ["RetryCount"] = SourceId.From("missing"),
                    }
                )
            )
        );
    }

    [Test]
    public void ReadOnlyDefaultComponent_IsRejected()
    {
        var writableStore = new InMemoryStateSource<AppSettings.Fragment>();
        var readOnlyStore = new InMemoryStateSource<AppSettings.Fragment>();
        StateSourceSet<AppSettings.Fragment> Components() =>
            new([
                new StateSource<AppSettings.Fragment>(
                    "writable",
                    writableStore,
                    writer: writableStore,
                    watcher: writableStore
                ),
                new StateSource<AppSettings.Fragment>("readonly", readOnlyStore),
            ]);

        Should.Throw<ArgumentException>(() =>
            new CompositeStateSource<AppSettings.Fragment>(
                Components(),
                SourceId.From("readonly")
            )
        );
        Should.Throw<ArgumentException>(() =>
            new CompositeStateSource<AppSettings.Fragment>(
                Components(),
                writePlan: StateWritePlan.DefaultTo(SourceId.From("readonly"))
            )
        );
    }

    [Test]
    public async Task RouteOnlyComposite_UnroutedMemberFailsWithoutChangingComponent()
    {
        var childStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(1),
                Label = Optional<string?>.Present("before"),
            }
        );
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "child",
                    childStore,
                    writer: childStore,
                    watcher: childStore
                ),
            ]),
            writePlan: new StateWritePlan(
                null,
                new Dictionary<string, SourceId>(StringComparer.Ordinal)
                {
                    ["RetryCount"] = SourceId.From("child"),
                }
            )
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.Add(composite.CreateSource("combined")))
            );
        });

        var state = context.GetRuntimeState<AppSettings>();
        using var edit = await state.OpenEditSessionAsync();
        edit.Value.Label = "after";

        var error = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await edit.CommitAsync()
        );
        error.Message.ShouldContain("no configured write owner");

        var current = (await childStore.ReadAsync()).Value!;
        current.RetryCount.Value.ShouldBe(1);
        current.Label.Value.ShouldBe("before");
    }

    [Test]
    public async Task NestedLongestPrefix_EditSessionSplitsAcrossOwners()
    {
        var (composite, childStore) = CreateNestedComposite();

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.Add(composite.CreateSource("combined")))
            );
        });

        var state = context.GetRuntimeState<AppSettings>();
        using var edit = await state.OpenEditSessionAsync();
        edit.Value.Database!.Host = "edited.db";
        edit.Value.Database.Port = 1111;
        await edit.CommitAsync();

        var childValue = (await childStore.ReadAsync()).Value!.Database.Value!;
        childValue.Host.Value.ShouldBe("edited.db");
        childValue.Port.Value.ShouldBe(1111);

        (await state.GetValueAsync()).Database!.Host.ShouldBe("edited.db");
        (await state.GetValueAsync()).Database!.Port.ShouldBe(1111);
    }

    [Test]
    public async Task NestedLongestPrefix_GeneratedPatchRoutesAcrossOwners()
    {
        var (composite, childStore) = CreateNestedComposite();
        await using var runtime = new ConfiglueRuntime<
            AppSettings,
            AppSettings.Fragment
        >(new StateSourceSet<AppSettings.Fragment>([composite.CreateSource("combined")]));

        await runtime.ApplyPatchesAsync([
            new StateSourcePatch(
                SourceId.From("combined"),
                new AppSettings.Patch
                {
                    Database = new DatabaseSettings.Patch
                    {
                        Host = FragmentOperation<string>.Set("routed.db"),
                        Port = FragmentOperation<int>.Set(2222),
                    },
                }
            ),
        ]);

        var childValue = (await childStore.ReadAsync()).Value!.Database.Value!;
        childValue.Host.Value.ShouldBe("routed.db");
        childValue.Port.Value.ShouldBe(2222);
    }

    [Test]
    public void NestedLongestPrefix_ResolvesMostSpecificOwner()
    {
        var databaseStore = new InMemoryStateSource<AppSettings.Fragment>();
        var portStore = new InMemoryStateSource<AppSettings.Fragment>();
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "database",
                    databaseStore,
                    writer: databaseStore,
                    watcher: databaseStore
                ),
                new StateSource<AppSettings.Fragment>(
                    "port",
                    portStore,
                    writer: portStore,
                    watcher: portStore
                ),
            ]),
            writePlan: new StateWritePlan(
                null,
                new Dictionary<string, SourceId>(StringComparer.Ordinal)
                {
                    ["Database"] = SourceId.From("database"),
                    ["Database.Port"] = SourceId.From("port"),
                }
            )
        );

        composite.ResolveWriteComponent("Database.Host").Id.ShouldBe(SourceId.From("database"));
        composite.ResolveWriteComponent("Database.Port").Id.ShouldBe(SourceId.From("port"));
    }

    private static (
        CompositeStateSource<AppSettings.Fragment> Composite,
        InMemoryStateSource<AppSettings.Fragment> ChildStore
    ) CreateNestedComposite()
    {
        var childStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment
                    {
                        Host = Optional<string>.Present("db.local"),
                        Port = Optional<int>.Present(5432),
                    }
                ),
            }
        );
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "child",
                    childStore,
                    writer: childStore,
                    watcher: childStore
                ),
            ]),
            writePlan: new StateWritePlan(
                null,
                new Dictionary<string, SourceId>(StringComparer.Ordinal)
                {
                    ["Database"] = SourceId.From("child"),
                    ["Database.Port"] = SourceId.From("child"),
                }
            )
        );
        return (composite, childStore);
    }
}
