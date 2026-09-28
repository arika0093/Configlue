using System.Collections.Concurrent;
using Configlue.Extensions.MSOptions;
using Configlue.Provider.Json;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue.Tests;

public sealed class ProfiledOptionsTests
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
            using (var firstProvider = CreateServiceProvider(filePath))
            {
                var profiles = firstProvider.GetRequiredService<
                    IConfiglueProfiledOptions<AppSettings>
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

            using (var restartedProvider = CreateServiceProvider(filePath))
            {
                var profiles = restartedProvider.GetRequiredService<
                    IConfiglueProfiledOptions<AppSettings>
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
        using var serviceProvider = CreateServiceProvider(
            Path.Combine(directory.FullPath, "profiles.json")
        );
        var profiles = serviceProvider.GetRequiredService<IConfiglueProfiledOptions<AppSettings>>();
        await profiles.GetProfileNamesAsync();

        var invalidNameRejected = false;
        try
        {
            await profiles.CreateProfileAsync("invalid__name");
        }
        catch (ArgumentException)
        {
            invalidNameRejected = true;
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

        (invalidNameRejected).ShouldBeTrue();
        (defaultRemovalRejected).ShouldBeTrue();
        (unknownProfileRejected).ShouldBeTrue();
        (duplicateProfileRejected).ShouldBeTrue();
    }

    [Test]
    public async Task ProfileCatalog_ConcurrentManagersDoNotOverwriteEachOther()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.FullPath, "profiles.json");
        using var firstProvider = CreateServiceProvider(filePath);
        using var secondProvider = CreateServiceProvider(filePath);
        var first = firstProvider.GetRequiredService<IConfiglueProfiledOptions<AppSettings>>();
        var second = secondProvider.GetRequiredService<IConfiglueProfiledOptions<AppSettings>>();
        await Task.WhenAll(
            first.GetProfileNamesAsync().AsTask(),
            second.GetProfileNamesAsync().AsTask()
        );

        var added = await Task.WhenAll(
            TryCreateProfileAsync(first, "First"),
            TryCreateProfileAsync(second, "Second")
        );

        (added.Count(static succeeded => succeeded)).ShouldBe(1);
        if (!added[0])
        {
            await first.CreateProfileAsync("First");
        }

        if (!added[1])
        {
            await second.CreateProfileAsync("Second");
        }

        using var restartedProvider = CreateServiceProvider(filePath);
        var restoredNames = await restartedProvider
            .GetRequiredService<IConfiglueProfiledOptions<AppSettings>>()
            .GetProfileNamesAsync();
        ((restoredNames))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "default", "First", "Second" }).OrderBy(static item => item));
    }

    [Test]
    public async Task ProfileCatalogReconciliationNotifiesOnlyCommittedAmbiguousActiveChanges()
    {
        var committedStore = new InMemoryStateStore<ConfiglueProfileCatalog>(
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
        var committedProfiles = new ConfiglueProfiledOptions<AppSettings, AppSettings.Fragment>(
            committedRegistry,
            new StateSource<ConfiglueProfileCatalog>(
                "catalog",
                committedStore,
                writer: committedWriter
            )
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

        var unchangedStore = new InMemoryStateStore<ConfiglueProfileCatalog>(
            new ConfiglueProfileCatalog
            {
                ProfileNames = ["default", "Work"],
                ActiveProfileName = "default",
            }
        );
        var unchangedRegistry = CreateProfileRegistry();
        var unchangedProfiles = new ConfiglueProfiledOptions<AppSettings, AppSettings.Fragment>(
            unchangedRegistry,
            new StateSource<ConfiglueProfileCatalog>(
                "catalog",
                unchangedStore,
                writer: new CommitThenThrowCatalogWriter(unchangedStore, commitBeforeThrow: false)
            )
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
        var catalogStore = new InMemoryStateStore<ConfiglueProfileCatalog>(
            new ConfiglueProfileCatalog
            {
                ProfileNames = ["default", "Work"],
                ActiveProfileName = "default",
            }
        );
        var innerRegistry = CreateProfileRegistry();
        var registry = new ThrowingNotificationDeferralRegistry(innerRegistry);
        var profiles = new ConfiglueProfiledOptions<AppSettings, AppSettings.Fragment>(
            registry,
            new StateSource<ConfiglueProfileCatalog>("catalog", catalogStore, writer: catalogStore)
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
    public async Task CustomDeferringRegistryAllowsProfileManagerReentrancyFromRegistryEvents()
    {
        var catalogStore = new InMemoryStateStore<ConfiglueProfileCatalog>(
            new ConfiglueProfileCatalog
            {
                ProfileNames = ["default"],
                ActiveProfileName = "default",
            }
        );
        var innerRegistry = CreateProfileRegistry();
        var registry = new ThrowingNotificationDeferralRegistry(innerRegistry);
        var profiles = new ConfiglueProfiledOptions<AppSettings, AppSettings.Fragment>(
            registry,
            new StateSource<ConfiglueProfileCatalog>("catalog", catalogStore, writer: catalogStore)
        );
        await profiles.GetProfileNamesAsync();
        await (await profiles.GetProfileAsync("default")).SaveAsync(patch =>
            patch.Label = "default-value"
        );

        var addedObservation = new TaskCompletionSource<(bool IsPublished, string? Value)>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        registry.ProfileAdded += (name, options) =>
        {
            if (name != "Work")
            {
                return;
            }

            var names = profiles
                .GetProfileNamesAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();
            var value = options
                .GetValueAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();
            addedObservation.TrySetResult((names.Contains(name), value.Label));
        };

        await profiles
            .CreateProfileAsync("Work", copyFrom: "default")
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        (await addedObservation.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(
            (true, "default-value")
        );

        var removedObservation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        registry.ProfileRemoved += name =>
        {
            if (name != "Work")
            {
                return;
            }

            var names = profiles
                .GetProfileNamesAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();
            removedObservation.TrySetResult(!names.Contains(name));
        };

        await profiles.RemoveProfileAsync("Work").AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        (await removedObservation.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
        await innerRegistry.DisposeAsync();
    }

    [Test]
    public async Task ProfileCatalogConflictRefreshRemovesStaleNamesFromEachRuntimeRegistry()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.FullPath, "profiles.json");
        using var firstProvider = CreateServiceProvider(filePath);
        using var secondProvider = CreateServiceProvider(filePath);
        var first = firstProvider.GetRequiredService<IConfiglueProfiledOptions<AppSettings>>();
        var second = secondProvider.GetRequiredService<IConfiglueProfiledOptions<AppSettings>>();
        var secondRegistry = secondProvider.GetRequiredService<
            IConfiglueOptionsRegistry<AppSettings>
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

    private static async Task<bool> TryCreateProfileAsync(
        IConfiglueProfiledOptions<AppSettings> profiles,
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

    private static ConfiglueOptionsRegistry<
        AppSettings,
        AppSettings.Fragment
    > CreateProfileRegistry() =>
        new(name =>
        {
            var store = new InMemoryStateStore<AppSettings.Fragment>();
            var source = new StateSource<AppSettings.Fragment>(name, store, writer: store);
            return new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
                new StateSourceSet<AppSettings.Fragment>([source])
            );
        });

    private static ServiceProvider CreateServiceProvider(string filePath)
    {
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddSingleton<FileResource>(_ => new FileResource(
            filePath,
            new FileResourceOptions { CreateBackup = false }
        ));
        services.AddConfiglueProfiledOptions<AppSettings, AppSettings.Fragment>(
            (provider, profileName) =>
            {
                var file = provider.GetRequiredService<FileResource>();
                var section = new JsonSectionResource(file, $"Profiles:{profileName}");
                var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
                    profileName,
                    section,
                    new JsonStateCodec()
                );
                return new StateSourceSet<AppSettings.Fragment>([source]);
            },
            provider =>
            {
                var file = provider.GetRequiredService<FileResource>();
                var section = new JsonSectionResource(file, "ProfileCatalog");
                return SerializedStateSource.FromResource<ConfiglueProfileCatalog>(
                    "profile-catalog",
                    section,
                    new JsonStateCodec()
                );
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
        InMemoryStateStore<ConfiglueProfileCatalog> inner,
        bool commitBeforeThrow
    ) : IStateWriter<ConfiglueProfileCatalog>
    {
        private int _shouldThrow = 1;

        public async ValueTask<StateWriteResult> WriteAsync(
            StateWriteRequest<ConfiglueProfileCatalog> request,
            CancellationToken cancellationToken = default
        )
        {
            if (Interlocked.Exchange(ref _shouldThrow, 0) == 1)
            {
                if (commitBeforeThrow)
                {
                    await inner.WriteAsync(request, cancellationToken);
                }
                throw new IOException("The catalog writer failed after an ambiguous commit.");
            }
            return await inner.WriteAsync(request, cancellationToken);
        }
    }

    private sealed class ThrowingNotificationDeferralRegistry(
        IConfiglueOptionsRegistry<AppSettings> inner
    )
        : IConfiglueOptionsRegistry<AppSettings>,
            IConfiglueOptionsRegistryNotificationDeferrer<AppSettings>
    {
        private int _throwOnAcquisition;
        private int _throwOnDisposal;

        public event Action<string, IWritableOptions<AppSettings>>? ProfileAdded
        {
            add => inner.ProfileAdded += value;
            remove => inner.ProfileAdded -= value;
        }

        public event Action<string>? ProfileRemoved
        {
            add => inner.ProfileRemoved += value;
            remove => inner.ProfileRemoved -= value;
        }

        public IReadOnlyCollection<string> ProfileNames => inner.ProfileNames;

        public IWritableOptions<AppSettings> Get(string profileName) => inner.Get(profileName);

        public bool TryGet(string profileName, out IWritableOptions<AppSettings>? options) =>
            inner.TryGet(profileName, out options);

        public bool TryAdd(string profileName) => inner.TryAdd(profileName);

        public bool TryRemove(string profileName) => inner.TryRemove(profileName);

        public ValueTask<bool> TryRemoveAsync(string profileName) =>
            inner.TryRemoveAsync(profileName);

        public void Clear() => inner.Clear();

        public ValueTask ClearAsync() => inner.ClearAsync();

        public void Dispose() => inner.Dispose();

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public void ThrowOnNextAcquisition() => Interlocked.Exchange(ref _throwOnAcquisition, 1);

        public void ThrowOnNextDisposal() => Interlocked.Exchange(ref _throwOnDisposal, 1);

        public IConfiglueOptionsRegistryNotificationDeferral<AppSettings> DeferNotifications()
        {
            if (Interlocked.Exchange(ref _throwOnAcquisition, 0) == 1)
            {
                throw new InvalidOperationException("Deferral acquisition failed.");
            }

            var innerScope = (
                (IConfiglueOptionsRegistryNotificationDeferrer<AppSettings>)inner
            ).DeferNotifications();
            return new DeferralScope(innerScope, this);
        }

        private sealed class DeferralScope(
            IConfiglueOptionsRegistryNotificationDeferral<AppSettings> innerScope,
            ThrowingNotificationDeferralRegistry owner
        ) : IConfiglueOptionsRegistryNotificationDeferral<AppSettings>
        {
            public void Cancel(IWritableOptions<AppSettings> runtime) => innerScope.Cancel(runtime);

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
}
