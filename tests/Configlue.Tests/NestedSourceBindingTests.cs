using Configlue.Provider.Json;
using Configlue.Testing;

namespace Configlue.Tests;

[ConfiglueModel("remote-database-contract", Version = 2)]
public partial class RemoteDatabaseContract
{
    public string Endpoint { get; set; } = "";

    public string HostName { get; set; } = "";

    public int Port { get; set; }
}

[ConfiglueModel("root-with-nested", Version = 1)]
public partial class RootWithNestedSettings
{
    public NestedSettings? Settings { get; set; } = new();
}

[ConfiglueModel("root-with-two-settings", Version = 1)]
public partial class RootWithTwoSettings
{
    public NestedSettings? Left { get; set; } = new();

    public NestedSettings? Right { get; set; } = new();
}

public sealed class NestedSourceBindingTests
{
    [Test]
    public async Task MountedPartialSubtreeOverridesOnlyPresentMembersAndRetainsProvenance()
    {
        var baseStore = new InMemoryStateStore<AppSettings.Fragment>(
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
        var remoteStore = new InMemoryStateStore<DatabaseSettings.Fragment>(
            new DatabaseSettings.Fragment { Host = Optional<string>.Present("remote.db") }
        );
        var remote = new StateSource<DatabaseSettings.Fragment>(
            "remote-database",
            remoteStore,
            priority: 100,
            writer: remoteStore,
            watcher: remoteStore,
            physicalOrigin: "database-row",
            resourceId: new ResourceId("database-resource")
        );
        var mounted = StateSourceProjection.Mount<DatabaseSettings.Fragment, AppSettings.Fragment>(
            remote,
            "Database"
        );
        (mounted.ResourceId).ShouldBe(new ResourceId("database-resource"));

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "defaults",
                            baseStore,
                            writer: baseStore
                        )
                    );
                    sources.AddMounted<
                        AppSettings,
                        AppSettings.Fragment,
                        DatabaseSettings,
                        DatabaseSettings.Fragment
                    >(remote, model => model.Database);
                })
            );
        });

        var options = (IConfiglueOptions<AppSettings>)context.GetOptions<AppSettings>();
        var value = await options.GetValueAsync();
        var database = (await options.GetDetailsAsync()).Database!;
        var host = database.Host;
        var port = database.Port;

        (value.RetryCount).ShouldBe(3);
        (value.Database!.Host).ShouldBe("remote.db");
        (value.Database.Port).ShouldBe(5432);
        (host.Source?.Key).ShouldBe(host.Sources[0].Source.Key);
        (host.Sources.First(item => item.IsPresent).Source.Locator).ShouldBe("database-row");
        (host.Sources.First(item => item.IsPresent).Value).ShouldBe("remote.db");
        (port.Source?.Key).ShouldBe(port.Sources[1].Source.Key);

        var changed = new TaskCompletionSource<AppSettings>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = options.OnChange(value => changed.TrySetResult(value));
        await remoteStore.WriteAsync(
            new StateWriteRequest<DatabaseSettings.Fragment>(
                new DatabaseSettings.Fragment
                {
                    Host = Optional<string>.Present("remote-updated.db"),
                }
            )
        );
        (await changed.Task.WaitAsync(TimeSpan.FromSeconds(5))).Database!.Host.ShouldBe(
            "remote-updated.db"
        );

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await options.SaveAsync(settings => settings.Database!.Host = "updated.db")
        );
        using (
            var edit = await options.OpenEditSessionAsync(
                new StateWritePlan(
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Database.Host"] = "remote-database",
                    }
                )
            )
        )
        {
            edit.Value.Database!.Host = "updated.db";
            await edit.CommitAsync();
        }
        (await remoteStore.ReadAsync()).Value!.Host.Value.ShouldBe("updated.db");
        (await baseStore.ReadAsync()).Value!.Database.Value!.Host.Value.ShouldBe("default.db");
    }

    [Test]
    public async Task WritableMountedSourceAppliesSparseNestedChangesToItsExistingContribution()
    {
        var baseStore = new InMemoryStateStore<AppSettings.Fragment>(
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
        var remoteStore = new InMemoryStateStore<DatabaseSettings.Fragment>(
            new DatabaseSettings.Fragment
            {
                Host = Optional<string>.Present("remote.db"),
                Port = Optional<int>.Present(7443),
            }
        );
        var remote = new StateSource<DatabaseSettings.Fragment>(
            "remote-database",
            remoteStore,
            priority: 100,
            writer: remoteStore
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.Add(new StateSource<AppSettings.Fragment>("defaults", baseStore));
                    sources.AddMounted<
                        AppSettings,
                        AppSettings.Fragment,
                        DatabaseSettings,
                        DatabaseSettings.Fragment
                    >(remote, model => model.Database);
                })
            );
        });

        var options = context.GetAdvancedOptions<AppSettings>();
        using (
            var edit = await options.OpenEditSessionAsync(
                new StateWritePlan(
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Database.Host"] = "remote-database",
                    }
                )
            )
        )
        {
            edit.Value.Database!.Host = "updated.remote.db";
            await edit.CommitAsync();
        }

        var remoteValue = (await remoteStore.ReadAsync()).Value!;
        (remoteValue.Host.Value).ShouldBe("updated.remote.db");
        (remoteValue.Port.Value).ShouldBe(7443);
        (await baseStore.ReadAsync()).Value!.Database.Value!.Host.Value.ShouldBe("default.db");
    }

    [Test]
    public async Task CurrentAwareReverseProjectionPreservesUnprojectedSourceFields()
    {
        var sourceStore = new InMemoryStateStore<RemoteDatabaseContract.Fragment>(
            new RemoteDatabaseContract.Fragment
            {
                Endpoint = Optional<string>.Present("keep-this-endpoint"),
                HostName = Optional<string>.Present("remote.db"),
                Port = Optional<int>.Present(7443),
            }
        );
        var source = new StateSource<RemoteDatabaseContract.Fragment>(
            "remote-contract",
            sourceStore,
            priority: 100,
            writer: sourceStore
        );
        var projected = StateSourceProjection.ProjectWithUpdate<
            RemoteDatabaseContract.Fragment,
            DatabaseSettings.Fragment
        >(
            source,
            static current => new DatabaseSettings.Fragment
            {
                Host = current.HostName,
                Port = current.Port,
            },
            static (previous, updated, current) =>
                new RemoteDatabaseContract.Fragment
                {
                    Endpoint = current?.Endpoint ?? Optional<string>.Missing,
                    HostName =
                        previous?.Host.IsPresent == true && !updated.Host.IsPresent
                            ? Optional<string>.Missing
                        : updated.Host.IsPresent ? updated.Host
                        : current?.HostName ?? Optional<string>.Missing,
                    Port = updated.Port.IsPresent
                        ? updated.Port
                        : current?.Port ?? Optional<int>.Missing,
                }
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.AddMounted<
                        AppSettings,
                        AppSettings.Fragment,
                        DatabaseSettings,
                        DatabaseSettings.Fragment
                    >(projected, model => model.Database, root => root.Database.Value!)
                )
            );
        });

        using (
            var edit = await context
                .GetAdvancedOptions<AppSettings>()
                .OpenEditSessionAsync(
                    new StateWritePlan(
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["Database.Host"] = "remote-contract",
                        }
                    )
                )
        )
        {
            edit.Value.Database!.Host = "updated.remote.db";
            await edit.CommitAsync();
        }

        var result = (await sourceStore.ReadAsync()).Value!;
        (result.Endpoint.Value).ShouldBe("keep-this-endpoint");
        (result.HostName.Value).ShouldBe("updated.remote.db");
        (result.Port.Value).ShouldBe(7443);

        var projectedOptions = new ConfiglueOptions<DatabaseSettings, DatabaseSettings.Fragment>(
            new StateSourceSet<DatabaseSettings.Fragment>([projected])
        );
        await projectedOptions.SaveAsync(
            new DatabaseSettings.Patch { Host = FragmentOperation<string>.Unset }
        );
        var afterUnset = (await sourceStore.ReadAsync()).Value!;
        (afterUnset.HostName.IsPresent).ShouldBeFalse();
        (afterUnset.Endpoint.Value).ShouldBe("keep-this-endpoint");
        (afterUnset.Port.Value).ShouldBe(7443);
    }

    [Test]
    public async Task WritableMountedSiblingSectionsBatchIntoOnePhysicalWrite()
    {
        var resource = new InMemoryResource();
        var codec = new JsonStateCodec<NestedSettings.Fragment>();
        var leftRawSource = SerializedStateSource.FromResource<NestedSettings.Fragment>(
            "left-settings",
            new JsonSectionResource(resource, "App:Left"),
            codec,
            priority: 10
        );
        var rightRawSource = SerializedStateSource.FromResource<NestedSettings.Fragment>(
            "right-settings",
            new JsonSectionResource(resource, "App:Right"),
            codec,
            priority: 10
        );
        var leftSource = StateSourceProjection.ProjectWithUpdate(
            leftRawSource,
            static fragment => fragment,
            MergeNestedSettings
        );
        var rightSource = StateSourceProjection.ProjectWithUpdate(
            rightRawSource,
            static fragment => fragment,
            MergeNestedSettings
        );
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<RootWithTwoSettings>(model =>
                model.Sources(sources =>
                {
                    sources.AddMounted<
                        RootWithTwoSettings,
                        RootWithTwoSettings.Fragment,
                        NestedSettings,
                        NestedSettings.Fragment
                    >(leftSource, model => model.Left, root => root.Left.Value!);
                    sources.AddMounted<
                        RootWithTwoSettings,
                        RootWithTwoSettings.Fragment,
                        NestedSettings,
                        NestedSettings.Fragment
                    >(rightSource, model => model.Right, root => root.Right.Value!);
                })
            );
        });

        using var edit = await context
            .GetAdvancedOptions<RootWithTwoSettings>()
            .OpenEditSessionAsync(
                new StateWritePlan(
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["Left.Label"] = "left-settings",
                        ["Right.Label"] = "right-settings",
                    }
                )
            );
        edit.Value.Left!.Label = "left";
        edit.Value.Right!.Label = "right";
        var result = await edit.CommitAsync();

        (result.MultiWriteResult!.PhysicalWriteCount).ShouldBe(1);
        (resource.WriteCount).ShouldBe(1);
        ((await leftSource.Reader.ReadAsync()).Value!.Label.Value).ShouldBe("left");
        ((await rightSource.Reader.ReadAsync()).Value!.Label.Value).ShouldBe("right");
    }

    [Test]
    public async Task MountedSourceMigrationAndProjectionRunBeforeTheNestedMount()
    {
        var sourceSchemaV1 = new StateSchemaMetadata("remote-database-contract", 1);
        var legacyReader = new FixedReader<RemoteDatabaseContract.Fragment>(
            StateReadResult<RemoteDatabaseContract.Fragment>.Success(
                new RemoteDatabaseContract.Fragment
                {
                    Endpoint = Optional<string>.Present("legacy.db"),
                },
                "legacy-revision",
                sourceSchemaV1
            )
        );
        var source = new StateSource<RemoteDatabaseContract.Fragment>(
            "legacy-database",
            legacyReader,
            physicalOrigin: "legacy://database"
        );
        var migrated = StateSourceProjection.Project<
            RemoteDatabaseContract.Fragment,
            DatabaseSettings.Fragment
        >(
            source,
            static fragment => new DatabaseSettings.Fragment
            {
                Host = fragment.HostName,
                Port = fragment.Port,
            },
            projectedSchema: DatabaseSettings.ConfiglueSchema.ToMetadata(),
            sourceMigrations: [new RemoteDatabaseContractV1ToV2Migration()],
            sourceSchema: RemoteDatabaseContract.ConfiglueSchema.ToMetadata()
        );
        var mounted = StateSourceProjection.Mount<DatabaseSettings.Fragment, AppSettings.Fragment>(
            migrated,
            "Database"
        );
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([mounted])
        );

        var result = await options.ReadAsync();

        (result.Value!.Database!.Host).ShouldBe("legacy.db");
        (result.Value.Database.Port).ShouldBe(7443);
        (result.Schema).ShouldBe(AppSettings.ConfiglueSchema.ToMetadata());
        (result.PhysicalOrigin).ShouldBe("legacy://database");
        (result.SourceId).ShouldBe("legacy-database");
    }

    [Test]
    public async Task MountedSourceCanTargetADeepNestedSubtree()
    {
        var baseStore = new InMemoryStateStore<RootWithNestedSettings.Fragment>(
            new RootWithNestedSettings.Fragment
            {
                Settings = Optional<NestedSettings.Fragment?>.Present(
                    new NestedSettings.Fragment
                    {
                        Label = Optional<string?>.Present("base-label"),
                        Inner = Optional<InnerSettingsV2.Fragment?>.Present(
                            new InnerSettingsV2.Fragment
                            {
                                Count = Optional<int>.Present(2),
                                Note = Optional<string?>.Present("base-note"),
                            }
                        ),
                    }
                ),
            }
        );
        var remoteStore = new InMemoryStateStore<InnerSettingsV2.Fragment>(
            new InnerSettingsV2.Fragment { Count = Optional<int>.Present(9) }
        );
        var remote = new StateSource<InnerSettingsV2.Fragment>(
            "remote-inner",
            remoteStore,
            priority: 100
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<RootWithNestedSettings>(model =>
                model.Sources(sources =>
                {
                    sources.Add(
                        new StateSource<RootWithNestedSettings.Fragment>("defaults", baseStore)
                    );
                    sources.AddMounted<
                        RootWithNestedSettings,
                        RootWithNestedSettings.Fragment,
                        InnerSettingsV2,
                        InnerSettingsV2.Fragment
                    >(remote, model => model.Settings!.Inner);
                })
            );
        });

        var options =
            (IConfiglueOptions<RootWithNestedSettings>)context.GetOptions<RootWithNestedSettings>();
        var value = await options.GetValueAsync();
        var count = (await options.GetDetailsAsync()).Settings!.Inner!.Count;

        (value.Settings!.Label).ShouldBe("base-label");
        (value.Settings.Inner!.Count).ShouldBe(9);
        (value.Settings.Inner.Note).ShouldBe("base-note");
        (count.Source?.Key).ShouldBe(count.Sources[0].Source.Key);
    }

    [Test]
    public async Task DistinctMountedSourcesCanShareOnePhysicalResourceIdentity()
    {
        var resourceId = new ResourceId("file:settings.json");
        var settingsSource = new StateSource<NestedSettings.Fragment>(
            "settings-section",
            new InMemoryStateStore<NestedSettings.Fragment>(
                new NestedSettings.Fragment { Label = Optional<string?>.Present("section-label") }
            ),
            priority: 100,
            physicalOrigin: "settings.json#Settings",
            resourceId: resourceId
        );
        var innerSource = new StateSource<InnerSettingsV2.Fragment>(
            "inner-section",
            new InMemoryStateStore<InnerSettingsV2.Fragment>(
                new InnerSettingsV2.Fragment { Count = Optional<int>.Present(11) }
            ),
            priority: 200,
            physicalOrigin: "settings.json#Settings:Inner",
            resourceId: resourceId
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<RootWithNestedSettings>(model =>
                model.Sources(sources =>
                {
                    sources.AddMounted<
                        RootWithNestedSettings,
                        RootWithNestedSettings.Fragment,
                        NestedSettings,
                        NestedSettings.Fragment
                    >(settingsSource, model => model.Settings);
                    sources.AddMounted<
                        RootWithNestedSettings,
                        RootWithNestedSettings.Fragment,
                        InnerSettingsV2,
                        InnerSettingsV2.Fragment
                    >(innerSource, model => model.Settings!.Inner);
                })
            );
        });

        var options =
            (IConfiglueOptions<RootWithNestedSettings>)context.GetOptions<RootWithNestedSettings>();
        var value = await options.GetValueAsync();
        var details = await options.GetDetailsAsync();
        var label = details.Settings!.Label;
        var count = details.Settings!.Inner!.Count;

        (value.Settings!.Label).ShouldBe("section-label");
        (value.Settings.Inner!.Count).ShouldBe(11);
        (label.Source?.Key).ShouldBe(label.Sources[1].Source.Key);
        (count.Source?.Key).ShouldBe(count.Sources[0].Source.Key);
        (label.Sources.First(item => item.IsPresent).Source.Locator).ShouldBe(
            "settings.json#Settings"
        );
        (count.Sources.First(item => item.IsPresent).Source.Locator).ShouldBe(
            "settings.json#Settings:Inner"
        );
    }

    [Test]
    public void MountRejectsUnknownAndMismatchedNestedPaths()
    {
        var sourceStore = new InMemoryStateStore<DatabaseSettings.Fragment>();
        var source = new StateSource<DatabaseSettings.Fragment>("database", sourceStore);

        Should.Throw<ArgumentException>(() =>
            StateSourceProjection.Mount<DatabaseSettings.Fragment, AppSettings.Fragment>(
                source,
                "Missing"
            )
        );
        Should.Throw<ArgumentException>(() =>
            StateSourceProjection.Mount<DatabaseSettings.Fragment, AppSettings.Fragment>(
                source,
                "RetryCount"
            )
        );
        Should.Throw<ArgumentException>(() =>
            StateSourceProjection.Mount<DatabaseSettings.Fragment, AppSettings.Fragment>(
                source,
                "Database.Unknown"
            )
        );

        var otherSourceStore = new InMemoryStateStore<InnerSettingsV2.Fragment>();
        var otherSource = new StateSource<InnerSettingsV2.Fragment>("inner", otherSourceStore);
        Should.Throw<ArgumentException>(() =>
            StateSourceProjection.Mount<InnerSettingsV2.Fragment, AppSettings.Fragment>(
                otherSource,
                "Database"
            )
        );
    }

    private sealed class RemoteDatabaseContractV1ToV2Migration
        : IStateSchemaMigration<RemoteDatabaseContract.Fragment>
    {
        public StateSchemaMetadata SourceSchema { get; } = new("remote-database-contract", 1);

        public StateSchemaMetadata TargetSchema { get; } =
            RemoteDatabaseContract.ConfiglueSchema.ToMetadata();

        public ValueTask<RemoteDatabaseContract.Fragment> MigrateAsync(
            RemoteDatabaseContract.Fragment value,
            CancellationToken cancellationToken = default
        ) =>
            ValueTask.FromResult(
                new RemoteDatabaseContract.Fragment
                {
                    Endpoint = value.Endpoint,
                    HostName = value.HostName.IsPresent ? value.HostName : value.Endpoint,
                    Port = value.Port.IsPresent ? value.Port : Optional<int>.Present(7443),
                }
            );
    }

    private sealed class FixedReader<T>(StateReadResult<T> result) : IStateReader<T>
    {
        public ValueTask<StateReadResult<T>> ReadAsync(
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(result);
    }

    private static NestedSettings.Fragment MergeNestedSettings(
        NestedSettings.Fragment? previous,
        NestedSettings.Fragment updated,
        NestedSettings.Fragment? current
    )
    {
        var merged = current ?? NestedSettings.Fragment.Empty;
        foreach (var member in updated.EnumeratePresentMembers())
        {
            merged = (NestedSettings.Fragment)merged.WithMember(member.Id, member.Value);
        }

        return merged;
    }
}
