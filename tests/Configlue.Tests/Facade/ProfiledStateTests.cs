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
                ((await (await profiles.GetProfileAsync("Work")).GetValueAsync()).Label).ShouldBe(
                    "Dark"
                );

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
    public async Task ProfileCatalog_SetActiveProfileNotifiesAfterCompletion()
    {
        var (catalog, _) = CreateProfileCatalog();
        var backing = CreateBackingStore();
        await using var context = CreateProfiledContext(catalog, backing);
        var profiles = context.GetProfiledState<AppSettings>();
        await profiles.GetProfileNamesAsync();
        await profiles.CreateProfileAsync("Work");

        var notifications = new List<string>();
        profiles.ActiveProfileChanged += notifications.Add;
        await profiles.SetActiveProfileAsync("Work");

        (notifications).ShouldBe(new[] { "Work" });
        (await profiles.GetActiveProfileNameAsync()).ShouldBe("Work");

        // Selecting the already-active profile is a no-op without a notification.
        await profiles.SetActiveProfileAsync("Work");
        (notifications).ShouldBe(new[] { "Work" });
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

        // Ordinary optimistic concurrency: a loser observes a conflict and retries.
        // A fully serialized schedule may let both initial writes succeed.
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
    public async Task ProfileCatalog_ConcurrentDuplicateCreateLeavesSingleProfile()
    {
        var (catalog, _) = CreateProfileCatalog();
        var backing = CreateBackingStore();
        await using var first = CreateProfiledContext(catalog, backing);
        await using var second = CreateProfiledContext(catalog, backing);
        var firstProfiles = first.GetProfiledState<AppSettings>();
        var secondProfiles = second.GetProfiledState<AppSettings>();
        await Task.WhenAll(
            firstProfiles.GetProfileNamesAsync().AsTask(),
            secondProfiles.GetProfileNamesAsync().AsTask()
        );

        var results = await Task.WhenAll(
            TryCreateProfileAsync(firstProfiles, "Dupe"),
            TryCreateProfileAsync(secondProfiles, "Dupe")
        );

        // Exactly one creation wins; the loser sees a duplicate or a conflict.
        // Both outcomes leave the catalog uncorrupted.
        (results.Count(static succeeded => succeeded)).ShouldBe(1);
        var names = await firstProfiles.GetProfileNamesAsync();
        (names.Count(static name => name == "Dupe")).ShouldBe(1);
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
        // The second manager observes the first commit on its next read.
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
    public async Task ProfileCatalog_CreateProfileCopyMaterializesUnloadedSourceOnDemand()
    {
        var (catalog, _) = CreateProfileCatalog();
        var backing = CreateBackingStore();
        await using var context = CreateProfiledContext(catalog, backing);
        var profiles = context.GetProfiledState<AppSettings>();
        await profiles.GetProfileNamesAsync();

        var source = await profiles.GetProfileAsync("default");
        await source.SaveAsync(patch => patch.RetryCount = 42);

        // Unloading the runtime does not delete catalog membership or backing data.
        var registry = context.GetStateRegistry<AppSettings>();
        (await registry.TryRemoveAsync("default")).ShouldBeTrue();
        (await profiles.GetProfileNamesAsync()).ShouldContain("default");

        await profiles.CreateProfileAsync("copied", copyFrom: "default");

        (await profiles.GetProfileNamesAsync()).ShouldContain("copied");
        ((await (await profiles.GetProfileAsync("copied")).GetValueAsync()).RetryCount).ShouldBe(
            42
        );
        ((await (await profiles.GetProfileAsync("default")).GetValueAsync()).RetryCount).ShouldBe(
            42
        );
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
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.OnChangeDebounce = TimeSpan.Zero;
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
        });

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
            new StateSource<ConfiglueProfileCatalog>(
                "catalog",
                store,
                new StateSourceOptions<ConfiglueProfileCatalog> { Writer = store, Watcher = store }
            ),
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
        return new StateSource<AppSettings.Fragment>(
            sourceId,
            store,
            new StateSourceOptions<AppSettings.Fragment> { Writer = store, Watcher = store }
        );
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
        catch (InvalidOperationException)
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

    private static ServiceProvider CreateServiceProvider(string filePath)
    {
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddSingleton<FileResource>(_ => new FileResource(
            filePath,
            new FileResourceOptions { CreateBackup = false }
        ));
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.OnChangeDebounce = TimeSpan.Zero;
                model.EnableProfiles(
                    (provider, _) =>
                    {
                        var file = provider!.GetRequiredService<FileResource>();
                        var section = new JsonSectionResource(file, "ProfileCatalog");
                        return new StateSource<ConfiglueProfileCatalog>(
                            "profile-catalog",
                            new SerializedSource<ConfiglueProfileCatalog>(
                                section,
                                StateCodecBinding.Dynamic(new JsonStateCodec()),
                                writer: (IResourceReader)section as IResourceWriter,
                                watcher: (IResourceReader)section as ISourceWatcher
                            ),
                            new StateSourceOptions<ConfiglueProfileCatalog>()
                        );
                    }
                );
                model.ConfigureSources(registration =>
                {
                    var file = registration.Services!.GetRequiredService<FileResource>();
                    var profileName = string.IsNullOrEmpty(registration.StateName)
                        ? "__fixed__"
                        : registration.StateName;
                    var section = new JsonSectionResource(file, $"Profiles:{profileName}");
                    var source = new StateSource<AppSettings.Fragment>(
                        profileName,
                        new SerializedSource<AppSettings.Fragment>(
                            section,
                            StateCodecBinding.Dynamic(new JsonStateCodec()),
                            writer: (IResourceReader)section as IResourceWriter,
                            watcher: (IResourceReader)section as ISourceWatcher
                        ),
                        new StateSourceOptions<AppSettings.Fragment>()
                    );
                    registration.Sources.Add(source);
                });
            });
        });
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
}
