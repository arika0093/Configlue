using System.Collections.Concurrent;
using Configlue.Codecs;
using Configlue.Extensions.MSOptions;
using Configlue.Provider.Json;
using Configlue.Sources;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue.Tests;

public sealed class ProfiledStateTests
{
    [Test]
    public async Task ProfileCatalog_PersistsProfilesValuesAndActiveSelectionAcrossServiceProviders()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Configlue.Tests",
            Guid.NewGuid().ToString("N")
        );
        var filePath = Path.Combine(directory, "profiles.json");
        Directory.CreateDirectory(directory);
        try
        {
            await using (var firstProvider = CreateServiceProvider(filePath))
            {
                var profiles = firstProvider.GetRequiredService<
                    IConfiglueProfiledState<AppSettings>
                >();
                var initialNames = await profiles.GetProfileNamesAsync();

                (profiles.DefaultProfileName).ShouldBe("default");
                ((initialNames))
                    .OrderBy(static item => item)
                    .ShouldBe((new[] { "default" }).OrderBy(static item => item));
                (await profiles.GetActiveProfileNameAsync()).ShouldBe("default");

                var defaultProfile = await profiles.GetActiveProfileAsync();
                await defaultProfile.SaveAsync(patch =>
                {
                    patch.RetryCount = 4;
                    patch.Label = "Light";
                });
                await profiles.CreateProfileAsync("Work", copyFrom: "default");
                await profiles.SetActiveProfileAsync("Work");
                ((await profiles.GetActiveValueAsync()).Label).ShouldBe("Light");

                var workProfile = await profiles.GetActiveProfileAsync();
                await workProfile.SaveAsync(patch =>
                {
                    patch.RetryCount = 9;
                    patch.Label = "Dark";
                });
                var defaultValue = await (
                    await profiles.GetProfileAsync("default")
                ).GetValueAsync();

                (defaultValue.Label).ShouldBe("Light");
                ((await profiles.GetActiveValueAsync()).Label).ShouldBe("Dark");
            }

            await using (var restartedProvider = CreateServiceProvider(filePath))
            {
                var profiles = restartedProvider.GetRequiredService<
                    IConfiglueProfiledState<AppSettings>
                >();
                var restoredNames = await profiles.GetProfileNamesAsync();

                ((restoredNames))
                    .OrderBy(static item => item)
                    .ShouldBe((new[] { "default", "Work" }).OrderBy(static item => item));
                (await profiles.GetActiveProfileNameAsync()).ShouldBe("Work");
                ((await profiles.GetActiveValueAsync()).Label).ShouldBe("Dark");
                (
                    restartedProvider
                        .GetRequiredService<IOptionsMonitor<AppSettings>>()
                        .Get("Work")
                        .Label
                ).ShouldBe("Dark");

                var activeChanged = new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                profiles.ActiveProfileChanged += profileName =>
                    activeChanged.TrySetResult(profileName);
                await profiles.RemoveProfileAsync("Work");

                (await activeChanged.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe("default");
                ((await profiles.GetProfileNamesAsync()))
                    .OrderBy(static item => item)
                    .ShouldBe((new[] { "default" }).OrderBy(static item => item));
                (await profiles.GetActiveProfileNameAsync()).ShouldBe("default");
                ((await profiles.GetActiveValueAsync()).Label).ShouldBe("Light");
            }

            var document = await File.ReadAllTextAsync(filePath);
            (document).ShouldContain("ProfileCatalog");
            (document).ShouldContain("default");
            (document).ShouldContain("Work");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public async Task ProfileCatalog_ValidatesNamesAndProtectsDefaultProfile()
    {
        using var directory = new TemporaryDirectory();
        await using var serviceProvider = CreateServiceProvider(
            Path.Combine(directory.FullPath, "profiles.json")
        );
        var profiles = serviceProvider.GetRequiredService<IConfiglueProfiledState<AppSettings>>();
        await profiles.GetProfileNamesAsync();

        await profiles.CreateProfileAsync("name__with__underscores");
        await profiles.CreateProfileAsync("name:with:colons");
        await profiles.CreateProfileAsync(nameof(ConfiglueProfileCatalog.ActiveProfileName));
        await profiles.CreateProfileAsync(nameof(ConfiglueProfileCatalog.ProfileNames));

        var names = await profiles.GetProfileNamesAsync();
        (names).ShouldContain("name__with__underscores");
        (names).ShouldContain("name:with:colons");
        (names).ShouldContain(nameof(ConfiglueProfileCatalog.ActiveProfileName));
        (names).ShouldContain(nameof(ConfiglueProfileCatalog.ProfileNames));
        (await profiles.GetProfileAsync("name:with:colons")).ShouldNotBeNull();

        var emptyNameRejected = false;
        try
        {
            await profiles.CreateProfileAsync("  ");
        }
        catch (ArgumentException)
        {
            emptyNameRejected = true;
        }

        var defaultRemovalRejected = false;
        try
        {
            await profiles.RemoveProfileAsync("default");
        }
        catch (InvalidOperationException)
        {
            defaultRemovalRejected = true;
        }

        var unknownProfileRejected = false;
        try
        {
            await profiles.GetProfileAsync("missing");
        }
        catch (KeyNotFoundException)
        {
            unknownProfileRejected = true;
        }

        await profiles.CreateProfileAsync("Work");
        var duplicateProfileRejected = false;
        try
        {
            await profiles.CreateProfileAsync("Work");
        }
        catch (InvalidOperationException)
        {
            duplicateProfileRejected = true;
        }

        (emptyNameRejected).ShouldBeTrue();
        (defaultRemovalRejected).ShouldBeTrue();
        (unknownProfileRejected).ShouldBeTrue();
        (duplicateProfileRejected).ShouldBeTrue();
    }

    [Test]
    public async Task ProfileCatalog_SameBaselineConcurrentWritesConflictExactlyOnce()
    {
        var store = new InMemoryStateSource<ConfiglueProfileCatalog>(
            new ConfiglueProfileCatalog
            {
                ProfileNames = ["default"],
                ActiveProfileName = "default",
            }
        );
        var barrier = new CatalogWriteBarrier();
        await using var firstRegistry = CreateProfileRegistry();
        await using var secondRegistry = CreateProfileRegistry();
        await using var first = CreateBarrieredProfiles(firstRegistry, store, barrier, manager: 0);
        await using var second = CreateBarrieredProfiles(
            secondRegistry,
            store,
            barrier,
            manager: 1
        );
        await Task.WhenAll(
            first.GetProfileNamesAsync().AsTask(),
            second.GetProfileNamesAsync().AsTask()
        );

        // Force both managers to observe the same baseline revision before either commits.
        barrier.Arm();

        bool[] added;
        try
        {
            added = await Task.WhenAll(
                    TryCreateProfileAsync(first, "First"),
                    TryCreateProfileAsync(second, "Second")
                )
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            barrier.Release();
        }

        (added.Count(static succeeded => succeeded)).ShouldBe(1);

        var reads = barrier.Reads;
        reads.Length.ShouldBe(2);
        reads
            .Select(static read => read.Manager)
            .OrderBy(static manager => manager)
            .ShouldBe(new[] { 0, 1 });
        reads[0].Revision.ShouldNotBeNull();
        reads[0].Revision.ShouldBe(reads[1].Revision);

        var writes = barrier.Writes;
        writes.Length.ShouldBe(2);
        (writes.Count(static write => write.Succeeded)).ShouldBe(1);
        foreach (var write in writes)
        {
            (write.Revision).ShouldBe(reads[0].Revision);
        }

        var refreshedRevision = (await store.ReadAsync(ConfiglueResourceContext.Default)).Revision;
        refreshedRevision.ShouldNotBe(reads[0].Revision);
        if (!added[0])
        {
            await first.CreateProfileAsync("First");
        }

        if (!added[1])
        {
            await second.CreateProfileAsync("Second");
        }

        var writesAfterRetry = barrier.Writes;
        writesAfterRetry.Length.ShouldBe(3);
        writesAfterRetry[^1].Succeeded.ShouldBeTrue();
        writesAfterRetry[^1].Revision.ShouldBe(refreshedRevision);
        var durable = await store.ReadAsync(ConfiglueResourceContext.Default);
        (durable.Value!.ProfileNames)
            .OrderBy(static name => name)
            .ShouldBe(new[] { "default", "First", "Second" }.OrderBy(static name => name));
    }

    [Test]
    public async Task ProfileCatalog_ConcurrentManagersDoNotOverwriteEachOther()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.FullPath, "profiles.json");
        await using var firstProvider = CreateServiceProvider(filePath);
        await using var secondProvider = CreateServiceProvider(filePath);
        var first = firstProvider.GetRequiredService<IConfiglueProfiledState<AppSettings>>();
        var second = secondProvider.GetRequiredService<IConfiglueProfiledState<AppSettings>>();
        await Task.WhenAll(
            first.GetProfileNamesAsync().AsTask(),
            second.GetProfileNamesAsync().AsTask()
        );

        var added = await Task.WhenAll(
            TryCreateProfileAsync(first, "First"),
            TryCreateProfileAsync(second, "Second")
        );

        // Scheduler order is not a contract: a fully serialized schedule may let both initial
        // writes succeed, while a same-baseline conflict makes one manager retry. Either way a
        // successfully committed profile must never be dropped.
        if (!added[0])
        {
            await first.CreateProfileAsync("First");
        }

        if (!added[1])
        {
            await second.CreateProfileAsync("Second");
        }

        await using var restartedProvider = CreateServiceProvider(filePath);
        var restoredNames = await restartedProvider
            .GetRequiredService<IConfiglueProfiledState<AppSettings>>()
            .GetProfileNamesAsync();
        ((restoredNames))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "default", "First", "Second" }).OrderBy(static item => item));
    }

    [Test]
    public async Task ProfileCatalog_SerializedInitialCallsBothSucceedAndRemainDurable()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "profiles.json");
        await using var firstProvider = CreateServiceProvider(path);
        await using var secondProvider = CreateServiceProvider(path);
        var first = firstProvider.GetRequiredService<IConfiglueProfiledState<AppSettings>>();
        var second = secondProvider.GetRequiredService<IConfiglueProfiledState<AppSettings>>();
        await first.GetProfileNamesAsync();
        (await TryCreateProfileAsync(first, "First")).ShouldBeTrue();
        // Establish the serialized schedule explicitly: the second manager observes the
        // first commit before its initial mutation, without waiting for a file watcher.
        (await second.GetProfileNamesAsync()).ShouldContain("First");
        (await TryCreateProfileAsync(second, "Second")).ShouldBeTrue();
        await using var restarted = CreateServiceProvider(path);
        (
            await restarted
                .GetRequiredService<IConfiglueProfiledState<AppSettings>>()
                .GetProfileNamesAsync()
        )
            .OrderBy(static name => name)
            .ShouldBe(new[] { "default", "First", "Second" }.OrderBy(static name => name));
    }

    [Test]
    public async Task ProfileCatalogReconciliationNotifiesOnlyCommittedAmbiguousActiveChanges()
    {
        var committedStore = new InMemoryStateSource<ConfiglueProfileCatalog>(
            new ConfiglueProfileCatalog
            {
                ProfileNames = ["default", "Work"],
                ActiveProfileName = "default",
            }
        );
        var committedWriter = new CommitThenThrowCatalogWriter(
            committedStore,
            commitBeforeThrow: true
        );
        var committedRegistry = CreateProfileRegistry();
        var committedProfiles = new ConfiglueProfiledState<AppSettings, AppSettings.Fragment>(
            committedRegistry,
            new StateSource<ConfiglueProfileCatalog>("catalog", committedStore, new StateSourceOptions<ConfiglueProfileCatalog> { Writer = committedWriter })
        );
        await committedProfiles.GetProfileNamesAsync();
        var committedNotifications = new ConcurrentQueue<(string Published, string Reentered)>();
        committedProfiles.ActiveProfileChanged += profileName =>
        {
            var reenteredName = committedProfiles
                .GetActiveProfileNameAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
            committedNotifications.Enqueue((profileName, reenteredName));
        };

        await Should.ThrowAsync<IOException>(async () =>
            await committedProfiles.SetActiveProfileAsync("Work")
        );
        (await committedStore.ReadAsync()).Value!.ActiveProfileName.ShouldBe("Work");
        var reconciledNames = await Task.WhenAll(
            Enumerable
                .Range(0, 8)
                .Select(_ => committedProfiles.GetActiveProfileNameAsync().AsTask())
        );
        reconciledNames.ShouldBe(Enumerable.Repeat("Work", 8).ToArray());
        committedNotifications.ShouldBe(new[] { ("Work", "Work") });
        (await committedProfiles.GetActiveProfileNameAsync()).ShouldBe("Work");
        committedNotifications.ShouldBe(new[] { ("Work", "Work") });
        await committedRegistry.DisposeAsync();

        var unchangedStore = new InMemoryStateSource<ConfiglueProfileCatalog>(
            new ConfiglueProfileCatalog
            {
                ProfileNames = ["default", "Work"],
                ActiveProfileName = "default",
            }
        );
        var unchangedRegistry = CreateProfileRegistry();
        var unchangedProfiles = new ConfiglueProfiledState<AppSettings, AppSettings.Fragment>(
            unchangedRegistry,
            new StateSource<ConfiglueProfileCatalog>("catalog", unchangedStore, new StateSourceOptions<ConfiglueProfileCatalog> { Writer = new CommitThenThrowCatalogWriter(unchangedStore, commitBeforeThrow: false) })
        );
        await unchangedProfiles.GetProfileNamesAsync();
        var unchangedNotifications = new ConcurrentQueue<string>();
        unchangedProfiles.ActiveProfileChanged += unchangedNotifications.Enqueue;

        await Should.ThrowAsync<IOException>(async () =>
            await unchangedProfiles.SetActiveProfileAsync("Work")
        );
        (await unchangedProfiles.GetActiveProfileNameAsync()).ShouldBe("default");
        unchangedNotifications.ShouldBeEmpty();
        await unchangedRegistry.DisposeAsync();
    }

    [Test]
    public async Task ProfileManagerReleasesItsGateWhenCustomNotificationDeferralFails()
    {
        var catalogStore = new InMemoryStateSource<ConfiglueProfileCatalog>(
            new ConfiglueProfileCatalog
            {
                ProfileNames = ["default", "Work"],
                ActiveProfileName = "default",
            }
        );
        var innerRegistry = CreateProfileRegistry();
        var registry = new ThrowingNotificationDeferralRegistry(innerRegistry);
        var profiles = new ConfiglueProfiledState<AppSettings, AppSettings.Fragment>(
            registry,
            new StateSource<ConfiglueProfileCatalog>("catalog", catalogStore, new StateSourceOptions<ConfiglueProfileCatalog> { Writer = catalogStore })
        );

        registry.ThrowOnNextAcquisition();
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await profiles.GetProfileNamesAsync()
        );
        (await profiles.GetProfileNamesAsync()).ShouldContain("Work");

        var activeNotification = new TaskCompletionSource<(string Published, string ReadBack)>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        profiles.ActiveProfileChanged += name =>
        {
            var readBack = profiles.GetActiveProfileNameAsync().AsTask().GetAwaiter().GetResult();
            activeNotification.TrySetResult((name, readBack));
        };
        registry.ThrowOnNextDisposal();
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await profiles.SetActiveProfileAsync("Work")
        );

        (await activeNotification.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(
            ("Work", "Work")
        );
        (await profiles.GetActiveProfileNameAsync()).ShouldBe("Work");
        await innerRegistry.DisposeAsync();
    }

    [Test]
    public async Task ProfileCatalogConflictRefreshRemovesStaleNamesFromEachRuntimeRegistry()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.FullPath, "profiles.json");
        await using var firstProvider = CreateServiceProvider(filePath);
        await using var secondProvider = CreateServiceProvider(filePath);
        var first = firstProvider.GetRequiredService<IConfiglueProfiledState<AppSettings>>();
        var second = secondProvider.GetRequiredService<IConfiglueProfiledState<AppSettings>>();
        var secondRegistry = secondProvider.GetRequiredService<
            IConfiglueStateRegistry<AppSettings>
        >();
        await Task.WhenAll(
            first.GetProfileNamesAsync().AsTask(),
            second.GetProfileNamesAsync().AsTask()
        );

        await first.CreateProfileAsync("Stale");
        if (!await TryCreateProfileAsync(second, "Other"))
        {
            await second.CreateProfileAsync("Other");
        }
        secondRegistry.TryGet("Stale", out _).ShouldBeTrue();

        try
        {
            await first.RemoveProfileAsync("Stale");
        }
        catch (StateConflictException)
        {
            await first.RemoveProfileAsync("Stale");
        }
        if (!await TryCreateProfileAsync(second, "Third"))
        {
            await second.CreateProfileAsync("Third");
        }

        secondRegistry.TryGet("Stale", out _).ShouldBeFalse();
        secondRegistry.TryGet("Other", out _).ShouldBeTrue();
        secondRegistry.TryGet("Third", out _).ShouldBeTrue();
    }

    [Test]
    public async Task ProfileCatalog_AdoptsMaterializedDynamicState()
    {
        var (catalog, _) = CreateProfileCatalog();
        var backing = CreateBackingStore();
        await using var context = CreateProfiledContext(catalog, backing);

        var registry = context.GetStateRegistry<AppSettings>();
        (await registry.TryAddAsync("Adopted")).ShouldBeTrue();
        var materialized = registry.Get("Adopted");

        var profiles = context.GetProfiledState<AppSettings>();
        await profiles.CreateProfileAsync("Adopted");

        (await profiles.GetProfileNamesAsync()).ShouldContain("Adopted");
        var adopted = await profiles.GetProfileAsync("Adopted");
        ReferenceEquals(adopted, materialized).ShouldBeTrue();
    }

    [Test]
    public async Task ProfileCatalog_RejectsNameConflictingWithFixedState()
    {
        var (catalog, _) = CreateProfileCatalog();
        var backing = CreateBackingStore();
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.StateName = "fixed";
                model.Sources(sources => sources.Add(CreateBackingSource(backing, "fixed")));
            });
            builder.Add<AppSettings>(model =>
            {
                model.EnableProfiles(catalog);
                model.ConfigureSources(registration =>
                    registration.Sources.Add(_ =>
                        CreateBackingSource(backing, registration.StateName)
                    )
                );
            });
        });

        var profiles = context.GetProfiledState<AppSettings>();
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await profiles.CreateProfileAsync("fixed")
        );
        (await profiles.GetProfileNamesAsync()).ShouldNotContain("fixed");

        await profiles.CreateProfileAsync("Work");
        (await profiles.GetProfileNamesAsync()).ShouldContain("Work");
    }

    [Test]
    public async Task ProfileCatalog_RemovalKeepsBackingDataForRematerialization()
    {
        var (catalog, _) = CreateProfileCatalog();
        var backing = CreateBackingStore();
        await using var context = CreateProfiledContext(catalog, backing);
        var profiles = context.GetProfiledState<AppSettings>();
        await profiles.GetProfileNamesAsync();
        await profiles.CreateProfileAsync("Work");

        var created = await profiles.GetProfileAsync("Work");
        await created.SaveAsync(patch => patch.Label = "persisted");

        await profiles.RemoveProfileAsync("Work");
        (await profiles.GetProfileNamesAsync()).ShouldNotContain("Work");
        await Should.ThrowAsync<KeyNotFoundException>(async () =>
            await profiles.GetProfileAsync("Work")
        );

        await profiles.CreateProfileAsync("Work");
        var rematerialized = await profiles.GetProfileAsync("Work");
        ((await rematerialized.GetValueAsync()).Label).ShouldBe("persisted");
    }

    [Test]
    public async Task ProfileCatalog_RegistryUnloadDoesNotDeleteCatalogMembership()
    {
        var (catalog, store) = CreateProfileCatalog();
        var backing = CreateBackingStore();
        await using var context = CreateProfiledContext(catalog, backing);
        var profiles = context.GetProfiledState<AppSettings>();
        await profiles.GetProfileNamesAsync();
        await profiles.CreateProfileAsync("Persistent");

        var registry = context.GetStateRegistry<AppSettings>();
        (await registry.TryRemoveAsync("Persistent")).ShouldBeTrue();
        registry.TryGet("Persistent", out _).ShouldBeFalse();

        // Force a catalog refresh after unload. Its registry notification is deferred
        // until synchronization completes, so this is an ordering barrier, not a delay.
        var refreshed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        registry.StateAdded += (name, _) =>
        {
            if (name == "RefreshMarker")
                refreshed.TrySetResult();
        };
        var currentCatalog = await store.ReadAsync(ConfiglueResourceContext.Default);
        await store.WriteAsync(
            ConfiglueResourceContext.Default,
            new StateWriteRequest<ConfiglueProfileCatalog>(
                new ConfiglueProfileCatalog
                {
                    ProfileNames = [.. currentCatalog.Value!.ProfileNames, "RefreshMarker"],
                    ActiveProfileName = currentCatalog.Value.ActiveProfileName,
                },
                Condition: RevisionCondition.FromRevision(currentCatalog.Revision)
            )
        );
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        registry.TryGet("Persistent", out _).ShouldBeFalse();

        (await profiles.GetProfileNamesAsync()).ShouldContain("Persistent");
        var rematerialized = await profiles.GetProfileAsync("Persistent");
        (registry.TryGet("Persistent", out var current)).ShouldBeTrue();
        ReferenceEquals(current, rematerialized).ShouldBeTrue();
    }

    [Test]
    public async Task ProfileCatalog_CreateProfileCopyRematerializesUnloadedDefault()
    {
        var (catalog, _) = CreateProfileCatalog();
        var backing = CreateBackingStore();
        await using var context = CreateProfiledContext(catalog, backing);
        var profiles = context.GetProfiledState<AppSettings>();
        await profiles.GetProfileNamesAsync();

        var source = await profiles.GetProfileAsync("default");
        await source.SaveAsync(patch => patch.RetryCount = 42);

        var registry = context.GetStateRegistry<AppSettings>();
        (await registry.TryRemoveAsync("default")).ShouldBeTrue();
        registry.TryGet("default", out _).ShouldBeFalse();
        // Unload is not a delete: catalog membership survives runtime removal.
        (await profiles.GetProfileNamesAsync()).ShouldContain("default");

        await profiles.CreateProfileAsync("copied", copyFrom: "default");

        (await profiles.GetProfileNamesAsync()).ShouldContain("copied");
        ((await (await profiles.GetProfileAsync("copied")).GetValueAsync()).RetryCount)
            .ShouldBe(42);
        ((await (await profiles.GetProfileAsync("default")).GetValueAsync()).RetryCount)
            .ShouldBe(42);
    }

    [Test]
    public async Task ProfileCatalog_CreateProfileCopyRematerializesUnloadedNonDefault()
    {
        var (catalog, _) = CreateProfileCatalog();
        var backing = CreateBackingStore();
        await using var context = CreateProfiledContext(catalog, backing);
        var profiles = context.GetProfiledState<AppSettings>();
        await profiles.GetProfileNamesAsync();
        await profiles.CreateProfileAsync("Work");

        var source = await profiles.GetProfileAsync("Work");
        await source.SaveAsync(patch => patch.RetryCount = 42);

        var registry = context.GetStateRegistry<AppSettings>();
        (await registry.TryRemoveAsync("Work")).ShouldBeTrue();
        registry.TryGet("Work", out _).ShouldBeFalse();
        (await profiles.GetProfileNamesAsync()).ShouldContain("Work");

        await profiles.CreateProfileAsync("Copied", copyFrom: "Work");

        ((await (await profiles.GetProfileAsync("Copied")).GetValueAsync()).RetryCount)
            .ShouldBe(42);
        ((await (await profiles.GetProfileAsync("Work")).GetValueAsync()).RetryCount)
            .ShouldBe(42);
    }

    [Test]
    public async Task ProfileCatalog_CreateProfileCopyRejectsUnknownSourceWithoutLeavingDestination()
    {
        var (catalog, _) = CreateProfileCatalog();
        var backing = CreateBackingStore();
        await using var context = CreateProfiledContext(catalog, backing);
        var profiles = context.GetProfiledState<AppSettings>();
        await profiles.GetProfileNamesAsync();

        await Should.ThrowAsync<KeyNotFoundException>(async () =>
            await profiles.CreateProfileAsync("copied", copyFrom: "missing")
        );

        (await profiles.GetProfileNamesAsync()).ShouldNotContain("copied");
        var registry = context.GetStateRegistry<AppSettings>();
        registry.TryGet("copied", out _).ShouldBeFalse();
    }

    [Test]
    public async Task ProfileCatalog_CreateProfileCopyFailureLeavesNoDestinationAndKeepsSource()
    {
        var (catalog, _) = CreateProfileCatalog();
        var backing = CreateBackingStore();
        await using var context = CreateProfiledContext(catalog, backing);
        var profiles = context.GetProfiledState<AppSettings>();
        await profiles.GetProfileNamesAsync();
        await profiles.CreateProfileAsync("Work");

        var source = await profiles.GetProfileAsync("Work");
        await source.SaveAsync(patch => patch.RetryCount = 42);

        var registry = context.GetStateRegistry<AppSettings>();
        var blocked = new BlockRematerializationRegistry(registry);
        var blockedProfiles = new ConfiglueProfiledState<
            AppSettings,
            AppSettings.Fragment
        >(
            blocked,
            catalog
        );
        try
        {
            await blockedProfiles.GetProfileNamesAsync();
            // Simulate a rematerialization conflict for the source only: after this
            // point TryAdd refuses "Work" while TryGet reports it as absent (unloaded below).
            blocked.Block("Work");
            (await registry.TryRemoveAsync("Work")).ShouldBeTrue();

            await Should.ThrowAsync<InvalidOperationException>(async () =>
                await blockedProfiles.CreateProfileAsync("copied", copyFrom: "Work")
            );

            (await blockedProfiles.GetProfileNamesAsync()).ShouldContain("Work");
            (await blockedProfiles.GetProfileNamesAsync()).ShouldNotContain("copied");
            registry.TryGet("copied", out _).ShouldBeFalse();
        }
        finally
        {
            await blockedProfiles.DisposeAsync();
        }
    }

    [Test]
    public async Task ProfileCatalogFactoryCanOwnAsyncDisposableResources()
    {
        var resource = new AsyncDisposableProbe();
        var (catalog, _) = CreateProfileCatalog();
        var backing = CreateBackingStore();
        await using (
            var context = ConfiglueApp.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model =>
                {
                    model.EnableProfiles(
                        (_, ownResource) =>
                        {
                            ownResource(resource);
                            return catalog;
                        }
                    );
                    model.ConfigureSources(registration =>
                        registration.Sources.Add(_ =>
                            CreateBackingSource(backing, registration.StateName)
                        )
                    );
                });
            })
        )
        {
            await context.GetProfiledState<AppSettings>().GetProfileNamesAsync();
            resource.DisposeAsyncCallCount.ShouldBe(0);
            await context.DisposeAsync();
        }

        resource.DisposeAsyncCallCount.ShouldBe(1);
        resource.DisposeCallCount.ShouldBe(0);
    }

    [Test]
    public async Task DiProfileCatalogFactoryCanOwnAsyncDisposableResources()
    {
        var resource = new AsyncDisposableProbe();
        var (catalog, _) = CreateProfileCatalog();
        var backing = CreateBackingStore();
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueProfiledState<AppSettings, AppSettings.Fragment>(
            (_, stateName) =>
                new StateSourceSet<AppSettings.Fragment>([CreateBackingSource(backing, stateName)]),
            (_, ownResource) =>
            {
                ownResource(resource);
                return catalog;
            },
            onChangeDebounce: TimeSpan.Zero
        );

        var provider = services.BuildServiceProvider();
        await using (provider)
        {
            await provider
                .GetRequiredService<IConfiglueProfiledState<AppSettings>>()
                .GetProfileNamesAsync();
            resource.DisposeAsyncCallCount.ShouldBe(0);
            await provider.DisposeAsync();
        }

        resource.DisposeAsyncCallCount.ShouldBe(1);
        resource.DisposeCallCount.ShouldBe(0);
    }

    private static (
        StateSource<ConfiglueProfileCatalog> Catalog,
        InMemoryStateSource<ConfiglueProfileCatalog> Store
    ) CreateProfileCatalog()
    {
        var store = new InMemoryStateSource<ConfiglueProfileCatalog>();
        return (
            new StateSource<ConfiglueProfileCatalog>("catalog", store, new StateSourceOptions<ConfiglueProfileCatalog> { Writer = store, Watcher = store }),
            store
        );
    }

    private static ConcurrentDictionary<
        string,
        InMemoryStateSource<AppSettings.Fragment>
    > CreateBackingStore() => new(StringComparer.Ordinal);

    private static ConfiglueContext CreateProfiledContext(
        StateSource<ConfiglueProfileCatalog> catalog,
        ConcurrentDictionary<string, InMemoryStateSource<AppSettings.Fragment>> backing
    ) =>
        ConfiglueApp.CreateContext(builder =>
            builder.Add<AppSettings>(model =>
            {
                model.EnableProfiles(catalog);
                model.ConfigureSources(registration =>
                    registration.Sources.Add(_ =>
                        CreateBackingSource(backing, registration.StateName)
                    )
                );
            })
        );

    private static StateSource<AppSettings.Fragment> CreateBackingSource(
        ConcurrentDictionary<string, InMemoryStateSource<AppSettings.Fragment>> backing,
        string stateName
    )
    {
        var store = backing.GetOrAdd(
            stateName,
            static _ => new InMemoryStateSource<AppSettings.Fragment>()
        );
        var sourceId = string.IsNullOrEmpty(stateName) ? "default" : stateName;
        return new StateSource<AppSettings.Fragment>(sourceId, store, new StateSourceOptions<AppSettings.Fragment> { Writer = store, Watcher = store });
    }

    private static async Task<bool> TryCreateProfileAsync(
        IConfiglueProfiledState<AppSettings> profiles,
        string profileName
    )
    {
        try
        {
            await profiles.CreateProfileAsync(profileName);
            return true;
        }
        catch (StateConflictException)
        {
            return false;
        }
    }

    private static ConfiglueOwnedStateRegistry<AppSettings> CreateProfileRegistry() =>
        new(
            name =>
            {
                var store = new InMemoryStateSource<AppSettings.Fragment>();
                var source = new StateSource<AppSettings.Fragment>(
                    name,
                    store,
                    new StateSourceOptions<AppSettings.Fragment> { Writer = store }
                );
                IWritableState<AppSettings> runtime =
                    new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
                        new StateSourceSet<AppSettings.Fragment>([source])
                    );
                return (runtime, []);
            },
            []
        );

    // The catalog reader/writer seam is the injectable StateSource. Wrapping the reader parks each
    // manager after it observes the shared baseline revision and releases both only once both have
    // read, so the subsequent conditional writes race on the same revision deterministically.
    private static ConfiglueProfiledState<
        AppSettings,
        AppSettings.Fragment
    > CreateBarrieredProfiles(
        IConfiglueStateRegistry<AppSettings> registry,
        InMemoryStateSource<ConfiglueProfileCatalog> store,
        CatalogWriteBarrier barrier,
        int manager
    ) =>
        new(
            registry,
            new StateSource<ConfiglueProfileCatalog>("catalog", new BarrierCatalogReader(manager, barrier, store), new StateSourceOptions<ConfiglueProfileCatalog> { Writer = new BarrierCatalogWriter(barrier, store) })
        );

    private static ServiceProvider CreateServiceProvider(string filePath)
    {
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddSingleton<FileResource>(_ => new FileResource(
            filePath,
            new FileResourceOptions { CreateBackup = false }
        ));
        services.AddConfiglueProfiledState<AppSettings, AppSettings.Fragment>(
            (provider, profileName) =>
            {
                var file = provider.GetRequiredService<FileResource>();
                var section = new JsonSectionResource(file, $"Profiles:{profileName}");
                var source = new StateSource<AppSettings.Fragment>(profileName, new SerializedSource<AppSettings.Fragment>(section, StateCodecBinding.Dynamic(new JsonStateCodec()), writer: (IResourceReader)section as IResourceWriter, watcher: (IResourceReader)section as ISourceWatcher), new StateSourceOptions<AppSettings.Fragment>());
                return new StateSourceSet<AppSettings.Fragment>([source]);
            },
            provider =>
            {
                var file = provider.GetRequiredService<FileResource>();
                var section = new JsonSectionResource(file, "ProfileCatalog");
                return new StateSource<ConfiglueProfileCatalog>("profile-catalog", new SerializedSource<ConfiglueProfileCatalog>(section, StateCodecBinding.Dynamic(new JsonStateCodec()), writer: (IResourceReader)section as IResourceWriter, watcher: (IResourceReader)section as ISourceWatcher), new StateSourceOptions<ConfiglueProfileCatalog>());
            },
            onChangeDebounce: TimeSpan.Zero
        );
        return services.BuildServiceProvider();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            FullPath = Path.Combine(
                Path.GetTempPath(),
                "Configlue.Tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(FullPath);
        }

        public string FullPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(FullPath))
            {
                Directory.Delete(FullPath, recursive: true);
            }
        }
    }

    private sealed class CommitThenThrowCatalogWriter(
        InMemoryStateSource<ConfiglueProfileCatalog> inner,
        bool commitBeforeThrow
    ) : ISourceWriter<ConfiglueProfileCatalog>
    {
        private int _shouldThrow = 1;

        public async ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<ConfiglueProfileCatalog> request,
            CancellationToken cancellationToken = default
        )
        {
            if (Interlocked.Exchange(ref _shouldThrow, 0) == 1)
            {
                if (commitBeforeThrow)
                {
                    await inner.WriteAsync(context, request, cancellationToken);
                }
                throw new IOException("The catalog writer failed after an ambiguous commit.");
            }
            return await inner.WriteAsync(context, request, cancellationToken);
        }
    }

    private sealed class ThrowingNotificationDeferralRegistry(
        IConfiglueStateRegistry<AppSettings> inner
    )
        : IConfiglueStateRegistry<AppSettings>,
            IConfiglueStateRegistryNotificationDeferrer<AppSettings>
    {
        private int _throwOnAcquisition;
        private int _throwOnDisposal;

        public event Action<string, IWritableState<AppSettings>>? StateAdded
        {
            add => inner.StateAdded += value;
            remove => inner.StateAdded -= value;
        }

        public event Action<string>? StateRemoved
        {
            add => inner.StateRemoved += value;
            remove => inner.StateRemoved -= value;
        }

        public IReadOnlyCollection<string> StateNames => inner.StateNames;

        public IWritableState<AppSettings> Get(string profileName) => inner.Get(profileName);

        public bool TryGet(string profileName, out IWritableState<AppSettings>? options) =>
            inner.TryGet(profileName, out options);

        public ValueTask<bool> TryAddAsync(string profileName) => inner.TryAddAsync(profileName);

        public ValueTask<bool> TryRemoveAsync(string profileName) =>
            inner.TryRemoveAsync(profileName);

        public ValueTask ClearAsync() => inner.ClearAsync();

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public void ThrowOnNextAcquisition() => Interlocked.Exchange(ref _throwOnAcquisition, 1);

        public void ThrowOnNextDisposal() => Interlocked.Exchange(ref _throwOnDisposal, 1);

        public IConfiglueStateRegistryNotificationDeferral<AppSettings> DeferNotifications()
        {
            if (Interlocked.Exchange(ref _throwOnAcquisition, 0) == 1)
            {
                throw new InvalidOperationException("Deferral acquisition failed.");
            }

            var innerScope = (
                (IConfiglueStateRegistryNotificationDeferrer<AppSettings>)inner
            ).DeferNotifications();
            return new DeferralScope(innerScope, this);
        }

        private sealed class DeferralScope(
            IConfiglueStateRegistryNotificationDeferral<AppSettings> innerScope,
            ThrowingNotificationDeferralRegistry owner
        ) : IConfiglueStateRegistryNotificationDeferral<AppSettings>
        {
            public void Cancel(IWritableState<AppSettings> runtime) => innerScope.Cancel(runtime);

            public void Dispose()
            {
                innerScope.Dispose();
                if (Interlocked.Exchange(ref owner._throwOnDisposal, 0) == 1)
                {
                    throw new InvalidOperationException("Deferral disposal failed.");
                }
            }
        }
    }

    private sealed class BlockRematerializationRegistry(
        IConfiglueStateRegistry<AppSettings> inner
    )
        : IConfiglueStateRegistry<AppSettings>,
            IConfiglueStateRegistryNotificationDeferrer<AppSettings>
    {
        private readonly HashSet<string> _blocked = new(StringComparer.Ordinal);

        public void Block(string profileName) => _blocked.Add(profileName);

        public event Action<string, IWritableState<AppSettings>>? StateAdded
        {
            add => inner.StateAdded += value;
            remove => inner.StateAdded -= value;
        }

        public event Action<string>? StateRemoved
        {
            add => inner.StateRemoved += value;
            remove => inner.StateRemoved -= value;
        }

        public IReadOnlyCollection<string> StateNames => inner.StateNames;

        public IWritableState<AppSettings> Get(string profileName) => inner.Get(profileName);

        public bool TryGet(string profileName, out IWritableState<AppSettings>? options) =>
            inner.TryGet(profileName, out options);

        public ValueTask<bool> TryAddAsync(string profileName) =>
            _blocked.Contains(profileName)
                ? new ValueTask<bool>(false)
                : inner.TryAddAsync(profileName);

        public ValueTask<bool> TryRemoveAsync(string profileName) =>
            inner.TryRemoveAsync(profileName);

        public ValueTask ClearAsync() => inner.ClearAsync();

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public IConfiglueStateRegistryNotificationDeferral<AppSettings> DeferNotifications() =>
            ((IConfiglueStateRegistryNotificationDeferrer<AppSettings>)inner).DeferNotifications();
    }

    private sealed class CatalogWriteBarrier
    {
        private readonly object _gate = new();
        private readonly List<(int Manager, string? Revision)> _reads = [];
        private readonly List<(string? Revision, bool Succeeded)> _writes = [];
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private bool _armed;
        private bool _released;
        private int _arrivals;

        public (int Manager, string? Revision)[] Reads
        {
            get
            {
                lock (_gate)
                {
                    return [.. _reads];
                }
            }
        }

        public (string? Revision, bool Succeeded)[] Writes
        {
            get
            {
                lock (_gate)
                {
                    return [.. _writes];
                }
            }
        }

        public void Arm()
        {
            lock (_gate)
            {
                _armed = true;
            }
        }

        public void Release()
        {
            lock (_gate)
            {
                _released = true;
                _release.TrySetResult();
            }
        }

        public Task ArriveReadAsync(int manager, string? revision)
        {
            lock (_gate)
            {
                if (!_armed || _released)
                {
                    return Task.CompletedTask;
                }

                _reads.Add((manager, revision));
                _arrivals++;
                if (_arrivals >= 2)
                {
                    _released = true;
                    _release.TrySetResult();
                    return Task.CompletedTask;
                }

                return _release.Task;
            }
        }

        public void RecordWrite(string? revision, bool succeeded)
        {
            lock (_gate)
            {
                _writes.Add((revision, succeeded));
            }
        }
    }

    private sealed class BarrierCatalogReader(
        int manager,
        CatalogWriteBarrier barrier,
        ISourceReader<ConfiglueProfileCatalog> inner
    ) : ISourceReader<ConfiglueProfileCatalog>
    {
        public async ValueTask<StateReadResult<ConfiglueProfileCatalog>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            var result = await inner.ReadAsync(context, cancellationToken).ConfigureAwait(false);
            await barrier.ArriveReadAsync(manager, result.Revision).ConfigureAwait(false);
            return result;
        }
    }

    private sealed class BarrierCatalogWriter(
        CatalogWriteBarrier barrier,
        ISourceWriter<ConfiglueProfileCatalog> inner
    ) : ISourceWriter<ConfiglueProfileCatalog>
    {
        public async ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<ConfiglueProfileCatalog> request,
            CancellationToken cancellationToken = default
        )
        {
            try
            {
                var result = await inner
                    .WriteAsync(context, request, cancellationToken)
                    .ConfigureAwait(false);
                barrier.RecordWrite(request.Condition.Revision, succeeded: true);
                return result;
            }
            catch (StateConflictException)
            {
                barrier.RecordWrite(request.Condition.Revision, succeeded: false);
                throw;
            }
        }
    }
}
