using Configlue.Provider.Json;
using Configlue.Testing;
using Configlue.Sources;

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

public sealed partial class NestedSourceBindingTests
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

        var options = (IConfiglueRuntimeState<AppSettings>)context.GetState<AppSettings>();
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

        await options.SaveAsync(settings => settings.Database!.Host = "updated.db");
        (await remoteStore.ReadAsync()).Value!.Host.Value.ShouldBe("updated.db");
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

        var options = context.GetRuntimeState<AppSettings>();
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
    public async Task OrdinaryPatchWritesAreRoutedToTheMountedSubtreeOwner()
    {
        var rootStore = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var databaseStore = new InMemoryStateStore<DatabaseSettings.Fragment>(
            new DatabaseSettings.Fragment
            {
                Host = Optional<string>.Present("remote.db"),
                Port = Optional<int>.Present(7443),
            }
        );
        var databaseSource = new StateSource<DatabaseSettings.Fragment>(
            "database-owner",
            databaseStore,
            writer: databaseStore
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.Add(
                        new StateSource<AppSettings.Fragment>("root", rootStore, writer: rootStore)
                    );
                    sources.AddMounted<
                        AppSettings,
                        AppSettings.Fragment,
                        DatabaseSettings,
                        DatabaseSettings.Fragment
                    >(databaseSource, settings => settings.Database);
                })
            );
        });

        var options = (IConfiglueRuntimeState<AppSettings>)context.GetState<AppSettings>();
        await options.SaveAsync(settings => settings.Database!.Host = "updated.remote.db");
        await options.SaveAsync(settings => settings.RetryCount = 9);

        var database = (await databaseStore.ReadAsync()).Value!;
        (database.Host.Value).ShouldBe("updated.remote.db");
        (database.Port.Value).ShouldBe(7443);
        (await rootStore.ReadAsync()).Value!.RetryCount.Value.ShouldBe(9);
    }

    [Test]
    public async Task MountedOwnerDoesNotRequireAnUnambiguousRootWriter()
    {
        var firstRootStore = new InMemoryStateStore<AppSettings.Fragment>();
        var secondRootStore = new InMemoryStateStore<AppSettings.Fragment>();
        var databaseStore = new InMemoryStateStore<DatabaseSettings.Fragment>(
            new DatabaseSettings.Fragment { Host = Optional<string>.Present("remote.db") }
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "first-root",
                            firstRootStore,
                            writer: firstRootStore
                        )
                    );
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "second-root",
                            secondRootStore,
                            writer: secondRootStore
                        )
                    );
                    sources.AddMounted<
                        AppSettings,
                        AppSettings.Fragment,
                        DatabaseSettings,
                        DatabaseSettings.Fragment
                    >(
                        new StateSource<DatabaseSettings.Fragment>(
                            "database-owner",
                            databaseStore,
                            writer: databaseStore
                        ),
                        settings => settings.Database
                    );
                })
            );
        });

        var options = (IConfiglueRuntimeState<AppSettings>)context.GetState<AppSettings>();
        await options.SaveAsync(settings => settings.Database!.Host = "updated.remote.db");

        (await databaseStore.ReadAsync()).Value!.Host.Value.ShouldBe("updated.remote.db");
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

    private sealed class FixedReader<T>(StateReadResult<T> result) : ISourceReader<T>
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
