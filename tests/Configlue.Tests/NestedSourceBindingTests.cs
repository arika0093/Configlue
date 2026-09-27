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

        var options = context.GetOptions<AppSettings>();
        var value = await options.GetValueAsync();
        var host = await options.ExplainAsync("Database.Host");
        var port = await options.ExplainAsync("Database.Port");

        (value.RetryCount).ShouldBe(3);
        (value.Database!.Host).ShouldBe("remote.db");
        (value.Database.Port).ShouldBe(5432);
        (host.HighestPrioritySourceId).ShouldBe("remote-database");
        (host.Contributions[0].PhysicalOrigin).ShouldBe("database-row");
        (host.Contributions[0].Revision).ShouldBe("1");
        (port.HighestPrioritySourceId).ShouldBe("defaults");

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

        await Should.ThrowAsync<StateConflictException>(async () =>
            await options.SaveAsync(settings => settings.Database!.Host = "updated.db")
        );
        (await baseStore.ReadAsync()).Value!.Database.Value!.Host.Value.ShouldBe("default.db");
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

        var options = context.GetOptions<RootWithNestedSettings>();
        var value = await options.GetValueAsync();
        var count = await options.ExplainAsync("Settings.Inner.Count");

        (value.Settings!.Label).ShouldBe("base-label");
        (value.Settings.Inner!.Count).ShouldBe(9);
        (value.Settings.Inner.Note).ShouldBe("base-note");
        (count.HighestPrioritySourceId).ShouldBe("remote-inner");
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

        var options = context.GetOptions<RootWithNestedSettings>();
        var value = await options.GetValueAsync();
        var label = await options.ExplainAsync("Settings.Label");
        var count = await options.ExplainAsync("Settings.Inner.Count");

        (value.Settings!.Label).ShouldBe("section-label");
        (value.Settings.Inner!.Count).ShouldBe(11);
        (label.HighestPrioritySourceId).ShouldBe("settings-section");
        (count.HighestPrioritySourceId).ShouldBe("inner-section");
        (label.Contributions[0].PhysicalOrigin).ShouldBe("settings.json#Settings");
        (count.Contributions[0].PhysicalOrigin).ShouldBe("settings.json#Settings:Inner");
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
}
