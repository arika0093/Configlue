using Configlue.Provider.Json;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed partial class NestedSourceBindingTests
{
    [Test]
    public async Task ExplicitOnlyMountedSourceIsNotAnOrdinaryWriteOwner()
    {
        var explicitStore = new InMemoryStateSource<DatabaseSettings.Fragment>(
            new DatabaseSettings.Fragment { Host = Optional<string>.Present("explicit.db") }
        );
        var ordinaryStore = new InMemoryStateSource<DatabaseSettings.Fragment>(
            new DatabaseSettings.Fragment { Host = Optional<string>.Present("ordinary.db") }
        );
        var explicitSource = new StateSource<DatabaseSettings.Fragment>("explicit-database", explicitStore, new StateSourceOptions<DatabaseSettings.Fragment> { Writer = explicitStore, ExplicitOnly = true });
        var ordinarySource = new StateSource<DatabaseSettings.Fragment>("ordinary-database", ordinaryStore, new StateSourceOptions<DatabaseSettings.Fragment> { Priority = 100, Writer = ordinaryStore });

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.AddMounted<
                        AppSettings,
                        AppSettings.Fragment,
                        DatabaseSettings,
                        DatabaseSettings.Fragment
                    >(explicitSource, settings => settings.Database);
                    sources.AddMounted<
                        AppSettings,
                        AppSettings.Fragment,
                        DatabaseSettings,
                        DatabaseSettings.Fragment
                    >(ordinarySource, settings => settings.Database);
                })
            );
        });

        var options = (IConfiglueRuntimeState<AppSettings>)context.GetState<AppSettings>();
        await options.SaveAsync(settings => settings.Database!.Host = "ordinary-updated.db");
        (await ordinaryStore.ReadAsync()).Value!.Host.Value.ShouldBe("ordinary-updated.db");
        (await explicitStore.ReadAsync()).Value!.Host.Value.ShouldBe("explicit.db");

        var explicitPatch = new AppSettings.Patch();
        explicitPatch.Database.Host = "explicit-updated.db";
        await options
            .Source(SourceKey<AppSettings>.Named("explicit-database"))
            .SaveAsync(explicitPatch);
        (await explicitStore.ReadAsync()).Value!.Host.Value.ShouldBe("explicit-updated.db");
    }

    [Test]
    public void DuplicateWritableMountedOwnersAreRejected()
    {
        var firstStore = new InMemoryStateSource<DatabaseSettings.Fragment>();
        var secondStore = new InMemoryStateSource<DatabaseSettings.Fragment>();

        Should.Throw<InvalidOperationException>(() =>
            ConfiglueApp.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.Sources(sources =>
                    {
                        sources.AddMounted<
                            AppSettings,
                            AppSettings.Fragment,
                            DatabaseSettings,
                            DatabaseSettings.Fragment
                        >(
                            new StateSource<DatabaseSettings.Fragment>("first-database", firstStore, new StateSourceOptions<DatabaseSettings.Fragment> { Writer = firstStore }),
                            settings => settings.Database
                        );
                        sources.AddMounted<
                            AppSettings,
                            AppSettings.Fragment,
                            DatabaseSettings,
                            DatabaseSettings.Fragment
                        >(
                            new StateSource<DatabaseSettings.Fragment>("second-database", secondStore, new StateSourceOptions<DatabaseSettings.Fragment> { Writer = secondStore }),
                            settings => settings.Database
                        );
                    })
                );
            })
        );
    }

    [Test]
    public async Task CurrentAwareReverseProjectionPreservesUnprojectedSourceFields()
    {
        var sourceStore = new InMemoryStateSource<RemoteDatabaseContract.Fragment>(
            new RemoteDatabaseContract.Fragment
            {
                Endpoint = Optional<string>.Present("keep-this-endpoint"),
                HostName = Optional<string>.Present("remote.db"),
                Port = Optional<int>.Present(7443),
            }
        );
        var source = new StateSource<RemoteDatabaseContract.Fragment>("remote-contract", sourceStore, new StateSourceOptions<RemoteDatabaseContract.Fragment> { Priority = 100, Writer = sourceStore });
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
                .GetRuntimeState<AppSettings>()
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

        var projectedOptions = new ConfiglueRuntime<DatabaseSettings, DatabaseSettings.Fragment>(
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
        IResourceReader leftSection = new JsonSectionResource(resource, "App:Left");
        var leftRawSource = new StateSource<NestedSettings.Fragment>(
            "left-settings",
            new SerializedSource<NestedSettings.Fragment>(
                leftSection,
                codec,
                writer: leftSection as IResourceWriter,
                watcher: leftSection as ISourceWatcher
            ),
            new StateSourceOptions<NestedSettings.Fragment> { Priority = 10 }
        );
        IResourceReader rightSection = new JsonSectionResource(resource, "App:Right");
        var rightRawSource = new StateSource<NestedSettings.Fragment>(
            "right-settings",
            new SerializedSource<NestedSettings.Fragment>(
                rightSection,
                codec,
                writer: rightSection as IResourceWriter,
                watcher: rightSection as ISourceWatcher
            ),
            new StateSourceOptions<NestedSettings.Fragment> { Priority = 10 }
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
            .GetRuntimeState<RootWithTwoSettings>()
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

        (result.PhysicalWriteCount).ShouldBe(1);
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
        var source = new StateSource<RemoteDatabaseContract.Fragment>("legacy-database", legacyReader, new StateSourceOptions<RemoteDatabaseContract.Fragment> { PhysicalOrigin = "legacy://database" });
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
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([mounted])
        );

        var result = await options.ReadAsync();

        (result.Value!.Database!.Host).ShouldBe("legacy.db");
        (result.Value.Database.Port).ShouldBe(7443);
        (result.Schema).ShouldBe(AppSettings.ConfiglueSchema.ToMetadata());
        (result.PhysicalOrigin).ShouldBe("legacy://database");
        (result.SourceId).ShouldBe(SourceId.From("legacy-database"));
    }

    [Test]
    public async Task MountedSourceCanTargetADeepNestedSubtree()
    {
        var baseStore = new InMemoryStateSource<RootWithNestedSettings.Fragment>(
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
        var remoteStore = new InMemoryStateSource<InnerSettingsV2.Fragment>(
            new InnerSettingsV2.Fragment { Count = Optional<int>.Present(9) }
        );
        var remote = new StateSource<InnerSettingsV2.Fragment>("remote-inner", remoteStore, new StateSourceOptions<InnerSettingsV2.Fragment> { Priority = 100 });

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<RootWithNestedSettings>(model =>
                model.Sources(sources =>
                {
                    sources.Add(
                        new StateSource<RootWithNestedSettings.Fragment>("defaults", baseStore, new StateSourceOptions<RootWithNestedSettings.Fragment>())
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
            (IConfiglueRuntimeState<RootWithNestedSettings>)
                context.GetState<RootWithNestedSettings>();
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
        var settingsSource = new StateSource<NestedSettings.Fragment>("settings-section", new InMemoryStateSource<NestedSettings.Fragment>(
                new NestedSettings.Fragment { Label = Optional<string?>.Present("section-label") }
            ), new StateSourceOptions<NestedSettings.Fragment> { Priority = 100, PhysicalOrigin = "settings.json#Settings", FixedResourceId = resourceId });
        var innerSource = new StateSource<InnerSettingsV2.Fragment>("inner-section", new InMemoryStateSource<InnerSettingsV2.Fragment>(
                new InnerSettingsV2.Fragment { Count = Optional<int>.Present(11) }
            ), new StateSourceOptions<InnerSettingsV2.Fragment> { Priority = 200, PhysicalOrigin = "settings.json#Settings:Inner", FixedResourceId = resourceId });

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
            (IConfiglueRuntimeState<RootWithNestedSettings>)
                context.GetState<RootWithNestedSettings>();
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
        var sourceStore = new InMemoryStateSource<DatabaseSettings.Fragment>();
        var source = new StateSource<DatabaseSettings.Fragment>("database", sourceStore, new StateSourceOptions<DatabaseSettings.Fragment>());

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

        var otherSourceStore = new InMemoryStateSource<InnerSettingsV2.Fragment>();
        var otherSource = new StateSource<InnerSettingsV2.Fragment>("inner", otherSourceStore, new StateSourceOptions<InnerSettingsV2.Fragment>());
        Should.Throw<ArgumentException>(() =>
            StateSourceProjection.Mount<InnerSettingsV2.Fragment, AppSettings.Fragment>(
                otherSource,
                "Database"
            )
        );
    }
}
