using Configlue.CompilerServices;
using Configlue.Testing;

namespace Configlue.Tests;

/// <summary>
/// Nested write-routing regression coverage for issue #220: schema-bound routing
/// stays in generated-ID space (no per-member dotted-string round trips) while
/// longest-prefix ownership semantics and string/dynamic APIs keep working.
/// </summary>
public sealed class NestedWriteRouting220Tests
{
    [Test]
    public async Task NestedComposite_RoutesNestedMembersThroughBoundPlan()
    {
        // NOTE: a single composite component mirrors the existing composite write
        // tests. Saving through a two-component composite currently fails in
        // PrepareCompositePatchAsync with a component revision-baseline mismatch
        // (see benchmarks README); that limitation is unrelated to #220 routing.
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
                new StateSource<AppSettings.Fragment>("child", childStore, new StateSourceOptions<AppSettings.Fragment> { Writer = childStore, Watcher = childStore }),
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
    public void NestedComposite_CompiledAndStringRoutingAgree()
    {
        var databaseStore = new InMemoryStateSource<AppSettings.Fragment>();
        var portStore = new InMemoryStateSource<AppSettings.Fragment>();
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("database", databaseStore, new StateSourceOptions<AppSettings.Fragment> { Writer = databaseStore, Watcher = databaseStore }),
                new StateSource<AppSettings.Fragment>("port", portStore, new StateSourceOptions<AppSettings.Fragment> { Writer = portStore, Watcher = portStore }),
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

        var schema = AppSettings.ConfiglueSchema;
        var bound = composite.GetBoundWritePlan(schema);
        var databasePath = ConfiglueMemberPath.FromNames(schema, "Database");
        var hostPath = ConfiglueMemberPath.FromNames(schema, "Database.Host");
        var portPath = ConfiglueMemberPath.FromNames(schema, "Database.Port");

        // Longest-prefix ownership through compiled paths.
        bound.ResolveSourceIdOrNull(hostPath).ShouldBe(SourceId.From("database"));
        bound.ResolveSourceIdOrNull(portPath).ShouldBe(SourceId.From("port"));
        bound.HasRouteBelow(databasePath).ShouldBeTrue();
        bound.HasRouteBelow(portPath).ShouldBeFalse();

        // Composite compiled overloads agree with the string/dynamic APIs.
        composite
            .ResolveWriteComponent(hostPath, bound)
            .Id.ShouldBe(composite.ResolveWriteComponent("Database.Host").Id);
        composite
            .ResolveWriteComponent(portPath, bound)
            .Id.ShouldBe(composite.ResolveWriteComponent("Database.Port").Id);
        composite
            .HasWriteRouteBelow(databasePath, bound)
            .ShouldBe(composite.HasWriteRouteBelow("Database"));
        composite
            .HasWriteRouteBelow(portPath, bound)
            .ShouldBe(composite.HasWriteRouteBelow("Database.Port"));

        // The bound plan is cached: binding is a one-time string parse.
        composite.GetBoundWritePlan(schema).ShouldBeSameAs(bound);
    }

    [Test]
    public async Task NestedRoutedEditSession_SplitsAcrossSourcesByLongestPrefix()
    {
        var root = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var nested = new InMemoryStateSource<AppSettings.Fragment>();
        await using var context = ConfiglueApp.CreateContext(builder =>
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                {
                    sources.Add(new StateSource<AppSettings.Fragment>("root", root, new StateSourceOptions<AppSettings.Fragment> { Writer = root }));
                    sources.Add(
                        new StateSource<AppSettings.Fragment>("nested", nested, new StateSourceOptions<AppSettings.Fragment> { Writer = nested })
                    );
                });
                model.Writes(write =>
                {
                    write.DefaultTo(SourceKey<AppSettings>.Named("root"));
                    write.Route(x => x.Database, SourceKey<AppSettings>.Named("nested"));
                    write.Route(x => x.Database!.Port, SourceKey<AppSettings>.Named("root"));
                });
            })
        );

        var options = context.GetRuntimeState<AppSettings>();
        using var edit = await options.OpenEditSessionAsync();
        edit.Value.Database!.Host = "nested.db";
        edit.Value.Database.Port = 9999;
        await edit.CommitAsync();

        (await nested.ReadAsync()).Value!.Database.Value!.Host.Value.ShouldBe("nested.db");
        (await root.ReadAsync()).Value!.Database.Value!.Port.Value.ShouldBe(9999);
    }

    [Test]
    public void NestedWritePlan_StringApisRemainAvailable()
    {
        var plan = StateWritePlan
            .For<AppSettings>()
            .DefaultTo(SourceKey<AppSettings>.Named("root"))
            .Route(x => x.Database, SourceKey<AppSettings>.Named("nested"))
            .Route(x => x.Database!.Port, SourceKey<AppSettings>.Named("root"))
            .Build();

        plan.PropertyRoutes.ContainsKey("Database").ShouldBeTrue();
        plan.ResolveSourceId("Database.Host").ShouldBe(SourceId.From("nested"));
        plan.ResolveSourceId("Database.Port").ShouldBe(SourceId.From("root"));
        plan.HasRouteBelow("Database").ShouldBeTrue();
        plan.HasRouteBelow("Database.Port").ShouldBeFalse();
    }
}
