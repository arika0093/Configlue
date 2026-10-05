using Configlue.Extensions.MSOptions;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue.Tests;

public sealed partial class StateRuntimeTests
{
    [Test]
    public async Task SourceProjection_MapsNestedSourceContractsAndRoutesWritesBack()
    {
        var remoteDatabase = new InMemoryStateSource<DatabaseSettings.Fragment>(
            new DatabaseSettings.Fragment { Host = Optional<string>.Present("remote.db") }
        );
        var defaults = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(3),
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment
                    {
                        Host = Optional<string>.Present("default.db"),
                        Port = Optional<int>.Present(5432),
                    }
                ),
            }
        );
        var databaseSource = new StateSource<DatabaseSettings.Fragment>("remote-database", remoteDatabase, new StateSourceOptions<DatabaseSettings.Fragment> { Priority = 100, Writer = remoteDatabase, PhysicalOrigin = "database-row" });
        var projectedSource = StateSourceProjection.Project<
            DatabaseSettings.Fragment,
            AppSettings.Fragment
        >(
            databaseSource,
            fragment => new AppSettings.Fragment
            {
                Database = Optional<DatabaseSettings.Fragment?>.Present(fragment),
            },
            root =>
                root.Database.IsPresent ? root.Database.Value! : DatabaseSettings.Fragment.Empty,
            AppSettings.ConfiglueSchema.ToMetadata()
        );
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([
            projectedSource,
            new StateSource<AppSettings.Fragment>("defaults", defaults, new StateSourceOptions<AppSettings.Fragment> { Priority = 0 }),
        ]);
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            sourceSet,
            StateWritePlan.DefaultTo(SourceId.From("remote-database"))
        );

        var resolved = await options.ReadAsync();
        await options.SaveAsync(
            new AppSettings.Patch
            {
                Database = FragmentOperation<DatabaseSettings.Fragment?>.Set(
                    new DatabaseSettings.Fragment
                    {
                        Host = Optional<string>.Present("saved.db"),
                        Port = Optional<int>.Present(7443),
                    }
                ),
            }
        );
        var savedSourceValue = await remoteDatabase.ReadAsync();
        var resolvedAfterWrite = await options.ReadAsync();

        (resolved.Value!.RetryCount).ShouldBe(3);
        (resolved.Value.Database!.Host).ShouldBe("remote.db");
        (resolved.Value.Database.Port).ShouldBe(5432);
        (resolved.PhysicalOrigin).ShouldBe("database-row");
        (savedSourceValue.Value!.Host.Value).ShouldBe("saved.db");
        (savedSourceValue.Value.Port.Value).ShouldBe(7443);
        (savedSourceValue.Value.Host.IsPresent).ShouldBeTrue();
        (resolvedAfterWrite.Value!.RetryCount).ShouldBe(3);
    }

    [Test]
    public async Task SourceProjection_MigratesItsContractBeforeProjectingIntoTheRootModel()
    {
        var sourceSchema = new StateSchemaMetadata("database-settings", 1);
        var legacy = new FixedStateReader<DatabaseSettings.Fragment>(
            StateReadResult<DatabaseSettings.Fragment>.Success(
                new DatabaseSettings.Fragment { Host = Optional<string>.Present("legacy.db") },
                "legacy-database-revision",
                sourceSchema
            )
        );
        var source = new StateSource<DatabaseSettings.Fragment>("legacy-database", legacy, new StateSourceOptions<DatabaseSettings.Fragment> { Priority = 100 });
        var projected = StateSourceProjection.Project<
            DatabaseSettings.Fragment,
            AppSettings.Fragment
        >(
            source,
            fragment => new AppSettings.Fragment
            {
                Database = Optional<DatabaseSettings.Fragment?>.Present(fragment),
            },
            projectedSchema: AppSettings.ConfiglueSchema.ToMetadata(),
            sourceMigrations: [new DatabaseV1ToV2Migration()],
            sourceSchema: DatabaseSettings.ConfiglueSchema.ToMetadata()
        );
        var sources = new StateSourceSet<AppSettings.Fragment>([projected]);
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(sources);

        var resolved = await options.ReadAsync();

        (resolved.Value!.Database!.Host).ShouldBe("legacy.db");
        (resolved.Value.Database.Port).ShouldBe(7400);
        (resolved.Schema).ShouldBe(AppSettings.ConfiglueSchema.ToMetadata());
    }

    [Test]
    public async Task Options_ExplainsEffectiveNestedValuesAndSparseSourceContributions()
    {
        var user = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment { Port = Optional<int>.Present(6432) }
                ),
            }
        );
        var defaults = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment
                    {
                        Host = Optional<string>.Present("default.db"),
                        Port = Optional<int>.Present(5432),
                    }
                ),
            }
        );
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([
            new StateSource<AppSettings.Fragment>("user", user, new StateSourceOptions<AppSettings.Fragment> { Priority = 100, PhysicalOrigin = "user-settings.json" }),
            new StateSource<AppSettings.Fragment>("defaults", defaults, new StateSourceOptions<AppSettings.Fragment> { Priority = 0, PhysicalOrigin = "defaults.json" }),
        ]);
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(sourceSet);

        var details = await options.GetDetailsAsync();

        (details.Database!.Port.Value).ShouldBe(6432);
        (details.Database.Port.Source?.Key).ShouldBe(details.Database.Port.Sources[0].Source.Key);
        (details.Database.Port.Sources.Count).ShouldBe(3);
        (details.Database.Port.Sources[0].Value).ShouldBe(6432);
        (details.Database.Port.Sources[0].Source.Locator).ShouldBe("user-settings.json");
        (details.Database.Host.Value).ShouldBe("default.db");
        (details.Database.Host.Source?.Key).ShouldBe(details.Database.Host.Sources[1].Source.Key);
        ((bool)details.Enabled.Value!).ShouldBeTrue();
        (details.Enabled.Sources[^1].Source.Kind).ShouldBe("model-defaults");
        (details.Enabled.Sources[^1].IsPresent).ShouldBeTrue();
    }

    // Member-level replace representative (#288). Per-element Append/SetUnion explanation
    // is an advanced opt-in via ConfiglueMergeProvenance and is not asserted on default Details.
    [Test]
    public async Task Options_ExplainsOnlyTheWinningSourceForReplacementCollections()
    {
        var user = new InMemoryStateSource<ReplaceCollectionSettings.Fragment>(
            new ReplaceCollectionSettings.Fragment
            {
                Values = Optional<IReadOnlyList<string>>.Present(["user"]),
            }
        );
        var defaults = new InMemoryStateSource<ReplaceCollectionSettings.Fragment>(
            new ReplaceCollectionSettings.Fragment
            {
                Values = Optional<IReadOnlyList<string>>.Present(["default"]),
            }
        );
        var options = new ConfiglueRuntime<
            ReplaceCollectionSettings,
            ReplaceCollectionSettings.Fragment
        >(
            new StateSourceSet<ReplaceCollectionSettings.Fragment>([
                new StateSource<ReplaceCollectionSettings.Fragment>("user", user, new StateSourceOptions<ReplaceCollectionSettings.Fragment> { Priority = 100 }),
                new StateSource<ReplaceCollectionSettings.Fragment>("defaults", defaults, new StateSourceOptions<ReplaceCollectionSettings.Fragment> { Priority = 0 }),
            ])
        );

        var details = await options.GetDetailsAsync();
        var values = details.Values!;

        (values.Value).ShouldBe(["user"]);
        (values.Source).ShouldNotBeNull();
        (values.Sources[0].IsPresent).ShouldBeTrue();
        (values.Sources[0].IsShadowed).ShouldBeFalse();
        (values.Sources[0].Value).ShouldBe(["user"]);
        (values.Sources[1].IsPresent).ShouldBeTrue();
        (values.Sources[1].IsShadowed).ShouldBeTrue();
        (values.Elements.Count).ShouldBe(0);
    }

    [Test]
    public async Task MigrateSourceAsync_CopiesOnlyTheSelectedContributionAfterSchemaMigration()
    {
        var legacySchema = new StateSchemaMetadata("app-settings", 1);
        var environment = new FixedStateReader<AppSettings.Fragment>(
            StateReadResult<AppSettings.Fragment>.Success(
                new AppSettings.Fragment { Label = Optional<string?>.Present("environment-value") },
                "environment-revision",
                AppSettings.ConfiglueSchema.ToMetadata()
            )
        );
        var legacy = new FixedStateReader<AppSettings.Fragment>(
            StateReadResult<AppSettings.Fragment>.Success(
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(9) },
                "legacy-revision",
                legacySchema
            )
        );
        var target = new InMemoryStateSource<AppSettings.Fragment>();
        var sources = new StateSourceSet<AppSettings.Fragment>([
            new StateSource<AppSettings.Fragment>("environment", environment, new StateSourceOptions<AppSettings.Fragment> { Priority = 100 }),
            new StateSource<AppSettings.Fragment>("legacy", legacy, new StateSourceOptions<AppSettings.Fragment> { Priority = 50 }),
            new StateSource<AppSettings.Fragment>("current", target, new StateSourceOptions<AppSettings.Fragment> { Priority = 0, Writer = target }),
        ]);
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            sources,
            migrations: [new AppSettingsV1ToV2Migration()]
        );

        var migration = await options.MigrateSourceAsync(
            SourceKey<AppSettings>.Named("legacy"),
            SourceKey<AppSettings>.Named("current")
        );
        var copied = await target.ReadAsync();

        migration.SourceId.ShouldBe(SourceId.From("legacy"));
        migration.TargetId.ShouldBe(SourceId.From("current"));
        (migration.SourceRevision).ShouldBe("legacy-revision");
        (migration.TargetRevision).ShouldBe("1");
        (copied.Value!.RetryCount.Value).ShouldBe(9);
        (copied.Value.Label.Value).ShouldBe("migrated");
        (copied.Value.Label.Value).ShouldNotBe("environment-value");
    }

    [Test]
    public async Task MigrateSourcesToTargetsAsync_MergesSelectedSourcesAndSkipsCompletedTargetsOnRetry()
    {
        var environment = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("environment-value") }
        );
        var user = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment { Port = Optional<int>.Present(6432) }
                ),
                Plugins = Optional<IReadOnlyList<string>>.Present(["user-plugin"]),
            }
        );
        var legacy = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(12),
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment { Host = Optional<string>.Present("legacy.db") }
                ),
            }
        );
        var primaryTarget = new InMemoryStateSource<AppSettings.Fragment>();
        var retryTarget = new InMemoryStateSource<AppSettings.Fragment>();
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("environment", environment, new StateSourceOptions<AppSettings.Fragment> { Priority = 200 }),
                new StateSource<AppSettings.Fragment>("user", user, new StateSourceOptions<AppSettings.Fragment> { Priority = 100 }),
                new StateSource<AppSettings.Fragment>("legacy", legacy, new StateSourceOptions<AppSettings.Fragment> { Priority = 50 }),
                new StateSource<AppSettings.Fragment>("primary", primaryTarget, new StateSourceOptions<AppSettings.Fragment> { Priority = 0, Writer = primaryTarget }),
                new StateSource<AppSettings.Fragment>("retry-only", retryTarget, new StateSourceOptions<AppSettings.Fragment> { Priority = -1, Writer = retryTarget }),
            ]),
            StateWritePlan.DefaultTo(SourceId.From("primary"))
        );
        var targets = new Dictionary<
            SourceKey<AppSettings>,
            Func<IConfiglueFragment, IConfiglueFragment>
        >()
        {
            [SourceKey<AppSettings>.Named("primary")] = static fragment => fragment,
            [SourceKey<AppSettings>.Named("retry-only")] =
                static fragment => new AppSettings.Fragment
                {
                    RetryCount = ((AppSettings.Fragment)fragment).RetryCount,
                },
        };
        IConfiglueRuntimeState<AppSettings> writableOptions = options;

        var firstRun = await writableOptions.MigrateSourcesToTargetsAsync(
            [SourceKey<AppSettings>.Named("legacy"), SourceKey<AppSettings>.Named("user")],
            targets
        );
        var primary = await primaryTarget.ReadAsync();
        var retryOnly = await retryTarget.ReadAsync();

        (primary.Value!.RetryCount.Value).ShouldBe(12);
        (primary.Value.Database.Value!.Host.Value).ShouldBe("legacy.db");
        (primary.Value.Database.Value.Port.Value).ShouldBe(6432);
        (primary.Value.Label.IsPresent).ShouldBeFalse();
        ((primary.Value.Plugins.Value!))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "user-plugin" }).OrderBy(static item => item));
        (retryOnly.Value!.RetryCount.Value).ShouldBe(12);
        (retryOnly.Value.Database.IsPresent).ShouldBeFalse();
        (firstRun.Targets.All(static result => !result.WasAlreadyCurrent)).ShouldBeTrue();

        var secondRun = await writableOptions.MigrateSourcesToTargetsAsync(
            [SourceKey<AppSettings>.Named("legacy"), SourceKey<AppSettings>.Named("user")],
            targets
        );

        (secondRun.Targets.All(static result => result.WasAlreadyCurrent)).ShouldBeTrue();
        (secondRun.Targets.Count).ShouldBe(2);
        (secondRun.Targets.All(static result => result.TargetRevision == "1")).ShouldBeTrue();
        secondRun.SourceIds.Select(static sourceId => sourceId.Value)
            .OrderBy(static item => item)
            .ShouldBe((new[] { "user", "legacy" }).OrderBy(static item => item));
    }

    [Test]
    public async Task MigrateSourcesToTargetsAsync_ResumesAfterALaterTargetFails()
    {
        var source = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(22) }
        );
        var firstTarget = new InMemoryStateSource<AppSettings.Fragment>();
        var secondTarget = new InMemoryStateSource<AppSettings.Fragment>();
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("source", source, new StateSourceOptions<AppSettings.Fragment> { Priority = 100, Writer = source }),
                new StateSource<AppSettings.Fragment>("first-target", firstTarget, new StateSourceOptions<AppSettings.Fragment> { Priority = 0, Writer = firstTarget }),
                new StateSource<AppSettings.Fragment>("second-target", secondTarget, new StateSourceOptions<AppSettings.Fragment> { Priority = -1, Writer = new FailOnceStateWriter<AppSettings.Fragment>(secondTarget) }),
            ]),
            defaultWritePlan: StateWritePlan.DefaultTo(SourceId.From("first-target"))
        );
        var targets = new Dictionary<SourceId, Func<IConfiglueFragment, IConfiglueFragment>>()
        {
            [SourceId.From("first-target")] = static fragment => fragment,
            [SourceId.From("second-target")] = static fragment => fragment,
        };
        IConfiglueRuntimeState<AppSettings> writableOptions = options;
        var failed = false;
        try
        {
            await writableOptions.MigrateSourcesToTargetsAsync(
                [SourceId.From("source")],
                targets,
                retireSources: true
            );
        }
        catch (IOException)
        {
            failed = true;
        }

        (failed).ShouldBeTrue();
        var afterFailure = await options.ReadAsync();
        afterFailure.Revisions!.TryGetRevision(SourceId.From("source"), out _).ShouldBeTrue();
        (afterFailure.Value!.RetryCount).ShouldBe(22);

        var resumed = await writableOptions.MigrateSourcesToTargetsAsync(
            [SourceId.From("source")],
            targets,
            retireSources: true
        );

        (resumed.Targets[0].WasAlreadyCurrent).ShouldBeTrue();
        (resumed.Targets[1].WasAlreadyCurrent).ShouldBeFalse();
        (resumed.SourcesRetired).ShouldBeTrue();
        resumed.RetiredSourceIds.Select(static sourceId => sourceId.Value)
            .OrderBy(static item => item)
            .ShouldBe((new[] { "source" }).OrderBy(static item => item));
        ((await secondTarget.ReadAsync()).Value!.RetryCount.Value).ShouldBe(22);

        var repeated = await writableOptions.MigrateSourcesToTargetsAsync(
            [SourceId.From("source")],
            targets,
            retireSources: true
        );
        (repeated.Targets.All(static target => target.WasAlreadyCurrent)).ShouldBeTrue();
        (repeated.SourcesRetired).ShouldBeTrue();
        ((await options.ReadAsync()).Value!.RetryCount).ShouldBe(22);
        (await options.ReadAsync())
            .Revisions!
            .TryGetRevision(SourceId.From("source"), out _)
            .ShouldBeFalse();

        await options.SaveAsync(settings => settings.RetryCount = 23);
        ((await source.ReadAsync()).Value!.RetryCount.Value).ShouldBe(22);
        ((await firstTarget.ReadAsync()).Value!.RetryCount.Value).ShouldBe(23);
        ((await options.ReadAsync()).Value!.RetryCount).ShouldBe(23);
    }

    [Test]
    public async Task MigrateSourcesToTargetsAsync_RefusesRetirementThatWouldChangeEffectiveModel()
    {
        var source = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(22) }
        );
        var target = new InMemoryStateSource<AppSettings.Fragment>();
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("source", source, new StateSourceOptions<AppSettings.Fragment> { Priority = 100 }),
                new StateSource<AppSettings.Fragment>("target", target, new StateSourceOptions<AppSettings.Fragment> { Priority = 0, Writer = target }),
            ])
        );
        IConfiglueRuntimeState<AppSettings> writableOptions = options;
        var projections = new Dictionary<SourceId, Func<IConfiglueFragment, IConfiglueFragment>>()
        {
            [SourceId.From("target")] = static _ => new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(25),
            },
        };
        var rejected = false;
        try
        {
            await writableOptions.MigrateSourcesToTargetsAsync(
                [SourceId.From("source")],
                projections,
                retireSources: true
            );
        }
        catch (StateConflictException)
        {
            rejected = true;
        }

        var resolved = await options.ReadAsync();
        (rejected).ShouldBeTrue();
        resolved.Revisions!.TryGetRevision(SourceId.From("source"), out _).ShouldBeTrue();
        (resolved.Value!.RetryCount).ShouldBe(22);
        ((await target.ReadAsync()).Value!.RetryCount.Value).ShouldBe(25);
    }
}
