using Configlue.Extensions.MSOptions;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue.Tests;

[ConfiglueModel("replace-collection-settings", Version = 1)]
public partial class ReplaceCollectionSettings
{
    public IReadOnlyList<string> Values { get; set; } = [];
}

public sealed class StateRuntimeTests
{
    [Test]
    public async Task FallbackStateSource_UsesOneRepresentationAndWritesToTheSelectedCandidate()
    {
        var canonical = new InMemoryStateStore<string>();
        var legacy = new InMemoryStateStore<string>("legacy");
        var fallback = new FallbackStateSource<string>(
            new StateSourceSet<string>([
                new(
                    "canonical",
                    canonical,
                    priority: 100,
                    fallbackCondition: StateFallbackCondition.NotFoundOrUnavailable,
                    writer: canonical,
                    watcher: canonical,
                    physicalOrigin: "settings.json"
                ),
                new(
                    "legacy",
                    legacy,
                    priority: 0,
                    writer: legacy,
                    watcher: legacy,
                    physicalOrigin: "settings.yaml"
                ),
            ])
        );
        var source = fallback.CreateSource("settings");

        var resolved = await source.Reader.ReadAsync();
        await source.Writer!.WriteAsync(
            new StateWriteRequest<string>(resolved.Value!, resolved.Revision, CheckRevision: true)
        );
        var legacyAfterWrite = await legacy.ReadAsync();
        var canonicalAfterWrite = await canonical.ReadAsync();

        (resolved.Status).ShouldBe(StateReadStatus.Success);
        (resolved.Value).ShouldBe("legacy");
        (resolved.SourceId).ShouldBe("legacy");
        (resolved.PhysicalOrigin).ShouldBe("settings.yaml");
        (resolved.Revisions!.Revisions.Count).ShouldBe(2);
        (resolved.Revisions.NestedRevisions.Count).ShouldBe(0);
        (fallback.SelectedSource!.Id).ShouldBe("legacy");
        (legacyAfterWrite.Value).ShouldBe("legacy");
        (canonicalAfterWrite.Status).ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public async Task FallbackStateSource_CanWriteToExplicitCanonicalCandidateWithoutLosingFallbackFields()
    {
        var canonical = new InMemoryStateStore<AppSettings.Fragment>();
        var legacy = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(11),
                Label = Optional<string?>.Present("legacy"),
            }
        );
        var fallback = new FallbackStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new(
                    "canonical",
                    canonical,
                    priority: 100,
                    fallbackCondition: StateFallbackCondition.NotFoundOrUnavailable,
                    writer: canonical,
                    watcher: canonical,
                    physicalOrigin: "settings.json"
                ),
                new(
                    "legacy",
                    legacy,
                    priority: 0,
                    writer: legacy,
                    watcher: legacy,
                    physicalOrigin: "settings.yaml"
                ),
            ]),
            writeSourceId: "canonical"
        );
        await using var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([fallback.CreateSource("settings")]),
            onChangeDebounce: TimeSpan.Zero
        );

        var initial = await options.ReadAsync();
        await options.SaveAsync(settings => settings.Label = "canonical");
        var canonicalAfterWrite = await canonical.ReadAsync();
        var legacyAfterWrite = await legacy.ReadAsync();
        var resolvedAfterWrite = await options.ReadAsync();

        (initial.Value!.RetryCount).ShouldBe(11);
        (initial.Value.Label).ShouldBe("legacy");
        (canonicalAfterWrite.Value!.RetryCount.Value).ShouldBe(11);
        (canonicalAfterWrite.Value.Label.Value).ShouldBe("canonical");
        (legacyAfterWrite.Value!.RetryCount.Value).ShouldBe(11);
        (legacyAfterWrite.Value.Label.Value).ShouldBe("legacy");
        (resolvedAfterWrite.SourceId).ShouldBe("settings");
        (resolvedAfterWrite.Value!.RetryCount).ShouldBe(11);
        (resolvedAfterWrite.Value.Label).ShouldBe("canonical");
        (fallback.SelectedSource!.Id).ShouldBe("canonical");
    }

    [Test]
    public async Task FallbackStateSource_WatchesForFailbackButIgnoresLowerPriorityChangesAfterSelection()
    {
        var canonical = new InMemoryStateStore<string>();
        var legacy = new InMemoryStateStore<string>("legacy");
        var fallback = new FallbackStateSource<string>(
            new StateSourceSet<string>([
                new(
                    "canonical",
                    canonical,
                    priority: 100,
                    fallbackCondition: StateFallbackCondition.NotFoundOrUnavailable,
                    writer: canonical,
                    watcher: canonical
                ),
                new("legacy", legacy, priority: 0, writer: legacy, watcher: legacy),
            ])
        );

        var initial = await fallback.ReadAsync();
        var failbackWait = fallback.WaitForChangeAsync(initial.Revision).AsTask();
        canonical.Set("canonical");
        await failbackWait.WaitAsync(TimeSpan.FromSeconds(5));
        var recovered = await fallback.ReadAsync();
        using var cancellation = new CancellationTokenSource();
        var lowerPriorityWait = fallback
            .WaitForChangeAsync(recovered.Revision, cancellation.Token)
            .AsTask();
        legacy.Set("stale legacy");
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        var lowerPriorityChangeWasIgnored = !lowerPriorityWait.IsCompleted;
        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await lowerPriorityWait);

        (initial.SourceId).ShouldBe("legacy");
        (recovered.SourceId).ShouldBe("canonical");
        (fallback.SelectedSource!.Id).ShouldBe("canonical");
        (lowerPriorityChangeWasIgnored).ShouldBeTrue();
    }

    [Test]
    public async Task FallbackStateSource_RejectsWriteWhenSelectedRepresentationChangedAfterRead()
    {
        var canonical = new InMemoryStateStore<string>();
        var legacy = new InMemoryStateStore<string>("legacy");
        var fallback = new FallbackStateSource<string>(
            new StateSourceSet<string>([
                new(
                    "canonical",
                    canonical,
                    priority: 100,
                    fallbackCondition: StateFallbackCondition.NotFoundOrUnavailable,
                    writer: canonical
                ),
                new("legacy", legacy, priority: 0, writer: legacy),
            ])
        );
        var initial = await fallback.ReadAsync();
        legacy.Set("changed");

        await Should.ThrowAsync<StateConflictException>(async () =>
            await fallback.WriteAsync(
                new StateWriteRequest<string>("stale write", initial.Revision, CheckRevision: true)
            )
        );

        ((await legacy.ReadAsync()).Value).ShouldBe("changed");
    }

    [Test]
    public async Task Resolver_FallsBackByPolicyAndWatchesHigherPrioritySourceForFailback()
    {
        var primary = new InMemoryStateStore<string>();
        primary.SetUnavailable();
        var fallback = new InMemoryStateStore<string>("local");
        var sources = new StateSourceSet<string>([
            new StateSource<string>(
                "remote",
                primary,
                priority: 100,
                fallbackCondition: StateFallbackCondition.NotFoundOrUnavailable,
                writer: primary,
                watcher: primary
            ),
            new StateSource<string>(
                "local",
                fallback,
                priority: 0,
                writer: fallback,
                watcher: fallback
            ),
        ]);
        var runtime = new CompositeStateRuntime<string>(sources, StateWriteRoute.To("local"));

        var resolved = await runtime.Reader.ReadAsync();
        await runtime.Writer.WriteAsync(
            new StateWriteRequest<string>("edited locally", resolved.Revision)
        );
        var localAfterWrite = await fallback.ReadAsync();
        var failbackWait = runtime.Watcher.WaitForChangeAsync(localAfterWrite.Revision).AsTask();
        primary.Set("remote");
        await failbackWait;
        var recovered = await runtime.Reader.ReadAsync();

        (resolved.Value).ShouldBe("local");
        (resolved.SourceId).ShouldBe("local");
        (resolved.Revisions!.Revisions.Count).ShouldBe(2);
        (localAfterWrite.Value).ShouldBe("edited locally");
        (recovered.Value).ShouldBe("remote");
        (recovered.SourceId).ShouldBe("remote");
        (runtime.Reader.ActiveSource!.Id).ShouldBe("remote");
    }

    [Test]
    public async Task CompositeWatcher_CancelsPendingWaitWhenLaterWatcherThrowsSynchronously()
    {
        var pendingWatcher = new PendingStateWatcher();
        var throwingWatcher = new SynchronousThrowingStateWatcher();
        var sources = new StateSourceSet<string>([
            new(
                "pending",
                new FixedStateReader<string>(StateReadResult<string>.Unavailable("primary")),
                priority: 100,
                fallbackCondition: StateFallbackCondition.Unavailable,
                watcher: pendingWatcher
            ),
            new(
                "throwing",
                new FixedStateReader<string>(
                    StateReadResult<string>.Success("fallback", "fallback")
                ),
                priority: 0,
                watcher: throwingWatcher
            ),
        ]);
        var runtime = new CompositeStateRuntime<string>(sources);
        await runtime.Reader.ReadAsync();

        var threw = false;
        try
        {
            await runtime.Watcher.WaitForChangeAsync("fallback");
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        (threw).ShouldBeTrue();
        (
            await pendingWatcher.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(5))
        ).ShouldBeTrue();
    }

    [Test]
    public async Task OptionsWatcher_CancelsPendingWaitWhenLaterWatcherThrowsSynchronously()
    {
        var pendingWatcher = new PendingStateWatcher();
        var throwingWatcher = new SynchronousThrowingStateWatcher();
        var sources = new StateSourceSet<AppSettings.Fragment>([
            new(
                "pending",
                new FixedStateReader<AppSettings.Fragment>(
                    StateReadResult<AppSettings.Fragment>.Unavailable("primary")
                ),
                priority: 100,
                fallbackCondition: StateFallbackCondition.Unavailable,
                watcher: pendingWatcher
            ),
            new(
                "throwing",
                new FixedStateReader<AppSettings.Fragment>(
                    StateReadResult<AppSettings.Fragment>.Success(
                        new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) },
                        "fallback"
                    )
                ),
                priority: 0,
                watcher: throwingWatcher
            ),
        ]);
        await using var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            sources,
            onChangeDebounce: TimeSpan.Zero
        );
        using var subscription = options.OnChange(static _ => { });

        await pendingWatcher.Started.WaitAsync(TimeSpan.FromSeconds(5));
        (
            await pendingWatcher.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(5))
        ).ShouldBeTrue();
    }

    [Test]
    public async Task DependencyInjection_ResolvesMergedOptionsAndSavesToConfiguredSource()
    {
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(4),
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment
                    {
                        Host = Optional<string>.Present("defaults.local"),
                    }
                ),
                Plugins = Optional<IReadOnlyList<string>>.Present(["base"]),
            }
        );
        var user = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Enabled = Optional<bool>.Present(false),
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment { Port = Optional<int>.Present(6432) }
                ),
                Plugins = Optional<IReadOnlyList<string>>.Present(["user"]),
            }
        );
        var sources = new StateSourceSet<AppSettings.Fragment>([
            new("user", user, priority: 100, writer: user),
            new("defaults", defaults, priority: 0),
        ]);
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueOptions<AppSettings, AppSettings.Fragment>(
            _ => sources,
            StateWriteRoute.To("user")
        );
        using var serviceProvider = services.BuildServiceProvider();
        var readOnly = serviceProvider.GetRequiredService<IReadOnlyOptions<AppSettings>>();
        var writable = serviceProvider.GetRequiredService<IWritableOptions<AppSettings>>();

        (ReferenceEquals(readOnly, writable)).ShouldBeTrue();
        var resolved = await readOnly.ReadAsync();
        var currentValue = await readOnly.GetValueAsync();
        var saveResult = await writable.SaveAsync(patch =>
        {
            patch.Enabled = true;
            patch.RetryCount = 10;
            patch.Label = "saved";
            patch.Database.Host = "saved.local";
            patch.Database.Port = 7443;
            patch.Plugins = new[] { "saved-plugin" };
        });
        var written = await user.ReadAsync();

        (resolved.Status).ShouldBe(StateReadStatus.Success);
        (resolved.SourceId).ShouldBe("user");
        (resolved.Revisions!.Revisions.Count).ShouldBe(2);
        (resolved.Value!.Enabled).ShouldBeFalse();
        (currentValue.RetryCount).ShouldBe(4);
        (resolved.Value.RetryCount).ShouldBe(4);
        (resolved.Value.Database!.Host).ShouldBe("defaults.local");
        (resolved.Value.Database.Port).ShouldBe(6432);
        ((resolved.Value.Plugins))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "base", "user" }).OrderBy(static item => item));
        (saveResult.Revision).ShouldBe("2");
        (written.Value!.RetryCount.Value).ShouldBe(10);
        (written.Value.Database!.Value!.Host.Value).ShouldBe("saved.local");
        ((written.Value.Plugins.Value!))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "saved-plugin" }).OrderBy(static item => item));
    }

    [Test]
    public async Task DependencyInjection_ProvidesMicrosoftOptionsAdapters()
    {
        var store = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(17) }
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new("default", store, writer: store)])
        );
        using var serviceProvider = services.BuildServiceProvider();

        var options = serviceProvider.GetRequiredService<IOptions<AppSettings>>();
        var monitor = serviceProvider.GetRequiredService<IOptionsMonitor<AppSettings>>();
        using var scope = serviceProvider.CreateScope();
        var snapshot = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<AppSettings>>();
        var firstSnapshot = snapshot.Value;
        var secondSnapshot = snapshot.Get(Options.DefaultName);

        (options.Value.RetryCount).ShouldBe(17);
        (monitor.CurrentValue.RetryCount).ShouldBe(17);
        (ReferenceEquals(firstSnapshot, secondSnapshot)).ShouldBeTrue();
        (firstSnapshot.RetryCount).ShouldBe(17);
    }

    [Test]
    public async Task OptionsSnapshot_CachesDefaultAndNamedProfilesWithinScope()
    {
        var defaultStore = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(17) }
        );
        var namedStore = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) }
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("default", defaultStore, writer: defaultStore),
            ])
        );
        services.AddConfiglueOptions<AppSettings, AppSettings.Fragment>(
            "custom",
            new StateSourceSet<AppSettings.Fragment>([
                new("custom", namedStore, writer: namedStore),
            ])
        );
        using var serviceProvider = services.BuildServiceProvider();

        using var firstScope = serviceProvider.CreateScope();
        var snapshot = firstScope.ServiceProvider.GetRequiredService<
            IOptionsSnapshot<AppSettings>
        >();
        (snapshot.Value.RetryCount).ShouldBe(17);
        (snapshot.Get("custom").RetryCount).ShouldBe(8);

        await serviceProvider
            .GetRequiredService<IWritableOptions<AppSettings>>()
            .SaveAsync(settings => settings.RetryCount = 23);
        await serviceProvider
            .GetRequiredKeyedService<IWritableOptions<AppSettings>>("custom")
            .SaveAsync(settings => settings.RetryCount = 19);

        (snapshot.Value.RetryCount).ShouldBe(17);
        (snapshot.Get("custom").RetryCount).ShouldBe(8);

        using var secondScope = serviceProvider.CreateScope();
        var updatedSnapshot = secondScope.ServiceProvider.GetRequiredService<
            IOptionsSnapshot<AppSettings>
        >();
        (updatedSnapshot.Value.RetryCount).ShouldBe(23);
        (updatedSnapshot.Get("custom").RetryCount).ShouldBe(19);
    }

    [Test]
    public async Task OptionsMonitor_ResolvesNamedProfilesAndPublishesTheirChanges()
    {
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var custom = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) }
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new("default", defaults, writer: defaults)]),
            onChangeDebounce: TimeSpan.Zero
        );
        services.AddConfiglueOptions<AppSettings, AppSettings.Fragment>(
            "custom",
            new StateSourceSet<AppSettings.Fragment>([
                new("custom", custom, writer: custom, watcher: custom),
            ]),
            onChangeDebounce: TimeSpan.Zero
        );
        using var serviceProvider = services.BuildServiceProvider();
        var monitor = serviceProvider.GetRequiredService<IOptionsMonitor<AppSettings>>();
        var changed = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var directChanged = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = monitor.OnChange(
            (value, name) =>
            {
                if (name == "custom")
                {
                    changed.TrySetResult(value.RetryCount);
                }
            }
        );
        using var directSubscription = serviceProvider
            .GetRequiredKeyedService<IReadOnlyOptions<AppSettings>>("custom")
            .OnChange(value => directChanged.TrySetResult(value.RetryCount));

        (monitor.Get("custom").RetryCount).ShouldBe(8);
        custom.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(12) });
        (await directChanged.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(12);
        (await changed.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(12);
    }

    [Test]
    public async Task OptionsMonitor_FollowsProfilesAddedToRuntimeRegistry()
    {
        var stores = new Dictionary<string, InMemoryStateStore<AppSettings.Fragment>>(
            StringComparer.Ordinal
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueOptionsRegistry<AppSettings, AppSettings.Fragment>(
            (_, profileName) =>
            {
                var store = new InMemoryStateStore<AppSettings.Fragment>(
                    new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) }
                );
                stores.Add(profileName, store);
                return new StateSourceSet<AppSettings.Fragment>([
                    new(profileName, store, writer: store, watcher: store),
                ]);
            },
            onChangeDebounce: TimeSpan.Zero
        );
        using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<IConfiglueOptionsRegistry<AppSettings>>();
        var monitor = serviceProvider.GetRequiredService<IOptionsMonitor<AppSettings>>();
        var changed = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = monitor.OnChange(
            (value, name) =>
            {
                if (name == "runtime")
                {
                    changed.TrySetResult(value.RetryCount);
                }
            }
        );

        (registry.TryAdd("runtime")).ShouldBeTrue();
        (monitor.Get("runtime").RetryCount).ShouldBe(4);
        stores["runtime"].Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(14) });
        (await changed.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(14);
    }

    [Test]
    public async Task OptionsMonitor_RemainsSubscribedWhenProfileIsReAddedDuringRemoval()
    {
        var stores = new Dictionary<string, InMemoryStateStore<AppSettings.Fragment>>(
            StringComparer.Ordinal
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueOptionsRegistry<AppSettings, AppSettings.Fragment>(
            (_, profileName) =>
            {
                var store = new InMemoryStateStore<AppSettings.Fragment>(
                    new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) }
                );
                stores[profileName] = store;
                return new StateSourceSet<AppSettings.Fragment>([
                    new(profileName, store, writer: store, watcher: store),
                ]);
            },
            onChangeDebounce: TimeSpan.Zero
        );
        using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<IConfiglueOptionsRegistry<AppSettings>>();
        (registry.TryAdd("runtime")).ShouldBeTrue();

        var removalEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var continueRemoval = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        // Pause before the monitor's removal handler so the replacement is added first.
        registry.ProfileRemoved += name =>
        {
            if (name == "runtime")
            {
                removalEntered.TrySetResult();
                continueRemoval.Task.GetAwaiter().GetResult();
            }
        };

        var monitor = serviceProvider.GetRequiredService<IOptionsMonitor<AppSettings>>();
        var replacementChanged = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = monitor.OnChange(
            (value, name) =>
            {
                if (name == "runtime" && value.RetryCount == 18)
                {
                    replacementChanged.TrySetResult(value.RetryCount);
                }
            }
        );

        var removal = Task.Run(() => registry.TryRemove("runtime"));
        Task<bool>? replacementAdd = null;
        try
        {
            await removalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            replacementAdd = Task.Run(() => registry.TryAdd("runtime"));
            (
                await Task.Run(() =>
                    SpinWait.SpinUntil(
                        () => registry.TryGet("runtime", out _),
                        TimeSpan.FromSeconds(5)
                    )
                )
            ).ShouldBeTrue();
        }
        finally
        {
            continueRemoval.TrySetResult();
        }

        (await removal).ShouldBeTrue();
        (await replacementAdd!).ShouldBeTrue();
        stores["runtime"].Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(18) });

        (await replacementChanged.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(18);
    }

    [Test]
    public async Task ApplyPatchesAsync_GroupsSiblingJsonSectionsIntoOnePhysicalWrite()
    {
        var resource = new InMemoryResource();
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var firstSection = new JsonSectionResource(resource, "App:First");
        var secondSection = new JsonSectionResource(resource, "App:Second");
        var firstSource = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "first",
            firstSection,
            codec,
            priority: 10
        );
        var secondSource = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "second",
            secondSection,
            codec,
            priority: 0
        );
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([firstSource, secondSource])
        );

        var result = await options.ApplyPatchesAsync([
            new StateSourcePatch(
                "first",
                new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }
            ),
            new StateSourcePatch(
                "second",
                new AppSettings.Patch { Label = FragmentOperation<string?>.Set("second section") }
            ),
        ]);
        var first = await firstSource.Reader.ReadAsync();
        var second = await secondSource.Reader.ReadAsync();
        var stored = await resource.ReadAsync();

        (result.Sources.Count).ShouldBe(2);
        (result.PhysicalWriteCount).ShouldBe(1);
        (resource.WriteCount).ShouldBe(1);
        (result.Sources.Select(static item => item.Revision).Distinct().Count()).ShouldBe(1);
        (first.Value!.RetryCount.Value).ShouldBe(7);
        (second.Value!.Label.Value).ShouldBe("second section");
        (stored.Status).ShouldBe(StateReadStatus.Success);
    }

    [Test]
    public async Task ApplyPatchesAsync_PersistsGroupedSectionsThroughFileResource()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"configlue-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var resource = new FileResource(Path.Combine(directory, "settings.json"));
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var first = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "first",
            new JsonSectionResource(resource, "App:First"),
            codec
        );
        var second = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "second",
            new JsonSectionResource(resource, "App:Second"),
            codec
        );
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([first, second])
        );

        try
        {
            var result = await options.ApplyPatchesAsync([
                new StateSourcePatch(
                    "first",
                    new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(5) }
                ),
                new StateSourcePatch(
                    "second",
                    new AppSettings.Patch { Label = FragmentOperation<string?>.Set("file batch") }
                ),
            ]);
            var firstState = await first.Reader.ReadAsync();
            var secondState = await second.Reader.ReadAsync();

            (result.PhysicalWriteCount).ShouldBe(1);
            (firstState.Value!.RetryCount.Value).ShouldBe(5);
            (secondState.Value!.Label.Value).ShouldBe("file batch");
            (firstState.Revision).ShouldBe(secondState.Revision);
        }
        finally
        {
            resource.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task ApplyPatchesAsync_RejectsOverlappingResourceScopesBeforeWriting()
    {
        var resource = new InMemoryResource();
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var parent = new JsonSectionResource(resource, "App");
        var child = new JsonSectionResource(resource, "App:Child");
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                SerializedStateSource.FromResource<AppSettings.Fragment>(
                    "parent",
                    parent,
                    codec,
                    priority: 10
                ),
                SerializedStateSource.FromResource<AppSettings.Fragment>(
                    "child",
                    child,
                    codec,
                    priority: 0
                ),
            ])
        );
        var failed = false;
        try
        {
            await options.ApplyPatchesAsync([
                new StateSourcePatch(
                    "parent",
                    new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }
                ),
                new StateSourcePatch(
                    "child",
                    new AppSettings.Patch { Label = FragmentOperation<string?>.Set("child") }
                ),
            ]);
        }
        catch (StateConflictException)
        {
            failed = true;
        }

        (failed).ShouldBeTrue();
        (resource.WriteCount).ShouldBe(0);

        var json = new JsonSectionResource(resource, "App:Json");
        var xml = new XmlSectionResource(resource, "App:Xml");
        var differentDomains = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                SerializedStateSource.FromResource<AppSettings.Fragment>("json", json, codec),
                SerializedStateSource.FromResource<AppSettings.Fragment>(
                    "xml",
                    xml,
                    new XmlStateCodec<AppSettings.Fragment>()
                ),
            ])
        );
        var domainConflict = false;
        try
        {
            await differentDomains.ApplyPatchesAsync([
                new StateSourcePatch(
                    "json",
                    new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(3) }
                ),
                new StateSourcePatch(
                    "xml",
                    new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(4) }
                ),
            ]);
        }
        catch (NotSupportedException)
        {
            domainConflict = true;
        }

        (domainConflict).ShouldBeTrue();
        (resource.WriteCount).ShouldBe(0);
    }

    [Test]
    public async Task ApplyPatchesAsync_BatchesXmlAndYamlSectionUpdates()
    {
        var xmlResource = new InMemoryResource();
        var xmlCodec = new XmlStateCodec<AppSettings.Fragment>();
        var xmlFirst = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "xml-first",
            new XmlSectionResource(xmlResource, "App:First"),
            xmlCodec
        );
        var xmlSecond = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "xml-second",
            new XmlSectionResource(xmlResource, "App:Second"),
            xmlCodec
        );
        var xmlOptions = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([xmlFirst, xmlSecond])
        );
        var xmlResult = await xmlOptions.ApplyPatchesAsync([
            new StateSourcePatch(
                "xml-first",
                new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(6) }
            ),
            new StateSourcePatch(
                "xml-second",
                new AppSettings.Patch { Label = FragmentOperation<string?>.Set("xml") }
            ),
        ]);

        var yamlResource = new InMemoryResource();
        var yamlCodec = new YamlStateCodec<AppSettings.Fragment>(
            modelSchema: AppSettings.FragmentSchema
        );
        var yamlFirst = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "yaml-first",
            new YamlSectionResource(yamlResource, "App:First"),
            yamlCodec
        );
        var yamlSecond = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "yaml-second",
            new YamlSectionResource(yamlResource, "App:Second"),
            yamlCodec
        );
        var yamlOptions = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([yamlFirst, yamlSecond])
        );
        var yamlResult = await yamlOptions.ApplyPatchesAsync([
            new StateSourcePatch(
                "yaml-first",
                new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(8) }
            ),
            new StateSourcePatch(
                "yaml-second",
                new AppSettings.Patch { Label = FragmentOperation<string?>.Set("yaml") }
            ),
        ]);

        (xmlResult.PhysicalWriteCount).ShouldBe(1);
        (xmlResource.WriteCount).ShouldBe(1);
        ((await xmlFirst.Reader.ReadAsync()).Value!.RetryCount.Value).ShouldBe(6);
        ((await xmlSecond.Reader.ReadAsync()).Value!.Label.Value).ShouldBe("xml");
        (yamlResult.PhysicalWriteCount).ShouldBe(1);
        (yamlResource.WriteCount).ShouldBe(1);
        ((await yamlFirst.Reader.ReadAsync()).Value!.RetryCount.Value).ShouldBe(8);
        ((await yamlSecond.Reader.ReadAsync()).Value!.Label.Value).ShouldBe("yaml");
    }

    [Test]
    public async Task Options_ReturnsModelDefaultsWhenEverySourceIsMissing()
    {
        var missing = new InMemoryStateStore<AppSettings.Fragment>();
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([
            new("optional", missing, writer: missing),
        ]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sourceSet);

        var result = await options.ReadAsync();
        var saved = await options.SaveAsync(patch => patch.RetryCount = 7);

        (result.Status).ShouldBe(StateReadStatus.Success);
        (result.Value!.Enabled).ShouldBeTrue();
        (result.Value.RetryCount).ShouldBe(3);
        (result.Value.Database!.Host).ShouldBe("localhost");
        (result.Value.Plugins).ShouldBeEmpty();
        (saved.Revision).ShouldBe("1");
    }

    [Test]
    public async Task Options_NotifiesSubscribersWhenAWatchedSourceChanges()
    {
        var user = new InMemoryStateStore<AppSettings.Fragment>();
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([
            new("user", user, priority: 100, watcher: user),
            new("defaults", defaults, priority: 0, watcher: defaults),
        ]);
        await using var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            sourceSet
        );
        var changedValue = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var resetValue = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = options.OnChange(value =>
        {
            if (value.RetryCount == 8)
            {
                changedValue.TrySetResult(value.RetryCount);
            }
            else if (value.RetryCount == 3)
            {
                resetValue.TrySetResult(value.RetryCount);
            }
        });

        defaults.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) });
        var updated = await changedValue.Task.WaitAsync(TimeSpan.FromSeconds(5));
        defaults.SetNotFound();
        var reset = await resetValue.Task.WaitAsync(TimeSpan.FromSeconds(5));

        (updated).ShouldBe(8);
        (reset).ShouldBe(3);
    }

    [Test]
    public async Task Options_CompositeReaderPreservesNestedIdentityAndPhysicalOrigin()
    {
        var primary = new OpaqueRevisionStateStore<AppSettings.Fragment>(
            StateReadStatus.Unavailable,
            physicalOrigin: "primary://settings"
        );
        var fallback = new OpaqueRevisionStateStore<AppSettings.Fragment>(
            StateReadStatus.Success,
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) },
            "fallback://settings"
        );
        var primaryRuntime = new CompositeStateRuntime<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("database", primary, watcher: primary, physicalOrigin: "primary://settings"),
            ])
        );
        var runtime = new CompositeStateRuntime<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new(
                    "remote",
                    primaryRuntime.Reader,
                    priority: 100,
                    fallbackCondition: StateFallbackCondition.Unavailable,
                    watcher: primaryRuntime.Watcher,
                    physicalOrigin: "logical://remote"
                ),
                new(
                    "local",
                    fallback,
                    priority: 0,
                    watcher: fallback,
                    physicalOrigin: "fallback://settings"
                ),
            ])
        );
        await using var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new(
                    "composite",
                    runtime.Reader,
                    writer: runtime.Writer,
                    watcher: runtime.Watcher,
                    physicalOrigin: "logical://settings"
                ),
            ]),
            onChangeDebounce: TimeSpan.Zero
        );

        var initial = await options.ReadAsync();
        var initialDetails = await options.GetDetailsAsync();
        using var staleSession = await options.OpenEditSessionAsync();
        staleSession.Value.RetryCount = 8;
        var changed = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = options.OnChange(value => changed.TrySetResult(value.RetryCount));

        (initial.Value!.RetryCount).ShouldBe(4);
        (initial.Revision).ShouldBe("opaque");
        (initial.PhysicalOrigin).ShouldBe("fallback://settings");
        (
            initialDetails
                .RetryCount.Sources.Single(s =>
                    s.IsPresent && s.Source.Locator == "fallback://settings"
                )
                .Source.Locator
        ).ShouldBe("fallback://settings");
        initial.Revisions!.NestedRevisions.Count.ShouldBe(1);
        var initialNested = initial.Revisions.NestedRevisions["composite"];
        (initialNested.TryGetRevision("remote", out _)).ShouldBeTrue();
        (initialNested.TryGetRevision("local", out _)).ShouldBeTrue();
        initialNested.NestedRevisions.Count.ShouldBe(1);
        (initialNested.NestedRevisions["remote"].TryGetRevision("database", out _)).ShouldBeTrue();
        await primary.WatchStarted.WaitAsync(TimeSpan.FromSeconds(5));

        primary.SetSuccess(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(9) },
            "primary://settings"
        );

        (await changed.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(9);
        var conflictThrown = false;
        try
        {
            await staleSession.CommitAsync();
        }
        catch (StateConflictException)
        {
            conflictThrown = true;
        }

        conflictThrown.ShouldBeTrue();
        var recovered = await options.ReadAsync();
        var recoveredDetails = await options.GetDetailsAsync();

        (recovered.Revision).ShouldBe("opaque");
        (recovered.PhysicalOrigin).ShouldBe("primary://settings");
        (
            recoveredDetails
                .RetryCount.Sources.Single(s =>
                    s.IsPresent && s.Source.Locator == "primary://settings"
                )
                .Source.Locator
        ).ShouldBe("primary://settings");
        recovered.Revisions!.NestedRevisions.Count.ShouldBe(1);
        var recoveredNested = recovered.Revisions.NestedRevisions["composite"];
        (recoveredNested.TryGetRevision("remote", out _)).ShouldBeTrue();
        (recoveredNested.TryGetRevision("local", out _)).ShouldBeFalse();
        recoveredNested.NestedRevisions.Count.ShouldBe(1);
        (
            recoveredNested.NestedRevisions["remote"].TryGetRevision("database", out _)
        ).ShouldBeTrue();
    }

    [Test]
    public async Task Options_DebouncesRapidSourceChangesAndReportsTheLatestValue()
    {
        var store = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([
            new("user", store, watcher: store),
        ]);
        await using var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            sourceSet,
            onChangeDebounce: TimeSpan.FromMilliseconds(150)
        );
        var notifications = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var latest = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = options.OnChange(value =>
        {
            notifications.Enqueue(value.RetryCount);
            latest.TrySetResult(value.RetryCount);
        });

        store.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) });
        await Task.Delay(TimeSpan.FromMilliseconds(30));
        store.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(5) });
        var notifiedValue = await latest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        (notifiedValue).ShouldBe(5);
        ((notifications.ToArray()))
            .OrderBy(static item => item)
            .ShouldBe((new[] { 5 }).OrderBy(static item => item));
    }

    [Test]
    public async Task EditSession_SavesDraftAndRejectsAStaleRevision()
    {
        var store = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([
            new("user", store, writer: store),
        ]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sourceSet);
        using var session = await options.OpenEditSessionAsync();
        session.Value.RetryCount = 6;
        var save = await session.CommitAsync();
        var saved = await store.ReadAsync();

        (session.IsCommitted).ShouldBeTrue();
        (save.Revision).ShouldBe("2");
        (saved.Value!.RetryCount.Value).ShouldBe(6);

        using var stale = await options.OpenEditSessionAsync();
        stale.Value.RetryCount = 7;
        store.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) });
        StateConflictException? conflict = null;
        try
        {
            await stale.CommitAsync();
        }
        catch (StateConflictException exception)
        {
            conflict = exception;
        }

        conflict.ShouldNotBeNull();
        conflict.Message.ShouldContain("RetryCount");
        (stale.IsCommitted).ShouldBeFalse();
    }

    [Test]
    public async Task EditSession_RebasesAChangeAfterAnUnrelatedPathChanges()
    {
        var store = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new("user", store, writer: store)])
        );
        using var session = await options.OpenEditSessionAsync();
        session.Value.RetryCount = 6;
        store.Set(
            new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(3),
                Enabled = Optional<bool>.Present(true),
            }
        );

        await session.CommitAsync();

        var resolved = (await options.ReadAsync()).Value!;
        resolved.RetryCount.ShouldBe(6);
        resolved.Enabled.ShouldBeTrue();
    }

    [Test]
    public async Task EditSession_RebasesNestedDisjointChanges()
    {
        var store = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment
                    {
                        Host = Optional<string>.Present("before"),
                        Port = Optional<int>.Present(5432),
                    }
                ),
            }
        );
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new("user", store, writer: store)])
        );
        using var session = await options.OpenEditSessionAsync();
        session.Value.Database!.Host = "session-host";
        store.Set(
            new AppSettings.Fragment
            {
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment
                    {
                        Host = Optional<string>.Present("before"),
                        Port = Optional<int>.Present(7443),
                    }
                ),
            }
        );

        await session.CommitAsync();

        var resolved = (await options.ReadAsync()).Value!;
        resolved.Database!.Host.ShouldBe("session-host");
        resolved.Database.Port.ShouldBe(7443);
    }

    [Test]
    public async Task SaveAsync_UpdatesACloneSynchronouslyOrAsynchronously()
    {
        var store = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(3),
                Plugins = Optional<IReadOnlyList<string>>.Present(["existing"]),
            }
        );
        var sources = new StateSourceSet<AppSettings.Fragment>([new("user", store, writer: store)]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sources);

        using (var edit = await options.OpenEditSessionAsync())
        {
            edit.Value.RetryCount++;
            edit.Value.Plugins = [.. edit.Value.Plugins, "sync"];
            await edit.CommitAsync();
        }
        using (var edit = await options.OpenEditSessionAsync())
        {
            await Task.Yield();
            edit.Value.RetryCount++;
            edit.Value.Plugins = [.. edit.Value.Plugins, "async"];
            await edit.CommitAsync();
        }

        var saved = await store.ReadAsync();
        var resolved = await options.ReadAsync();
        (saved.Value!.RetryCount.Value).ShouldBe(5);
        ((saved.Value.Plugins.Value!))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "existing", "sync", "async" }).OrderBy(static item => item));
        (resolved.Value!.RetryCount).ShouldBe(5);
    }

    [Test]
    public async Task SaveAsync_DoesNotRetainCallerOwnedCollectionReferences()
    {
        var store = new InMemoryStateStore<AppSettings.Fragment>();
        var sources = new StateSourceSet<AppSettings.Fragment>([new("user", store, writer: store)]);
        await using var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sources);
        var plugins = new List<string> { "before-save" };

        await options.SaveAsync(patch => patch.Plugins = plugins);
        plugins.Add("after-save");

        var saved = await options.ReadAsync();
        saved.Value!.Plugins.ShouldBe(new[] { "before-save" });
    }

    [Test]
    public async Task EditSession_RebasesAfterAnUnrelatedSourceChanges()
    {
        var user = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { Enabled = Optional<bool>.Present(true) }
        );
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var sources = new StateSourceSet<AppSettings.Fragment>([
            new("user", user, priority: 100, writer: user),
            new("defaults", defaults, priority: 0),
        ]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sources);
        using var session = await options.OpenEditSessionAsync();
        session.Value.Enabled = false;
        defaults.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) });

        await session.CommitAsync();

        var storedUser = await user.ReadAsync();
        var resolved = await options.ReadAsync();
        (storedUser.Value!.Enabled.Value).ShouldBeFalse();
        (storedUser.Revision).ShouldBe("2");
        resolved.Value!.RetryCount.ShouldBe(8);
        resolved.Value.Enabled.ShouldBeFalse();
    }

    [Test]
    public async Task EditSession_WritesOnlySemanticChangesToTheSelectedContribution()
    {
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(3),
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment
                    {
                        Host = Optional<string>.Present("defaults.db"),
                        Port = Optional<int>.Present(5432),
                    }
                ),
            }
        );
        var user = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Label = Optional<string?>.Present("user label"),
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment { Port = Optional<int>.Present(6432) }
                ),
            }
        );
        var sources = new StateSourceSet<AppSettings.Fragment>([
            new("user", user, priority: 100, writer: user),
            new("defaults", defaults),
        ]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sources);

        using var session = await options.OpenEditSessionAsync();
        session.Value.RetryCount = 7;
        session.Value.Database!.Port = 7443;
        await session.CommitAsync();

        var storedUser = (await user.ReadAsync()).Value!;
        var resolved = (await options.ReadAsync()).Value!;
        (storedUser.RetryCount.IsPresent).ShouldBeTrue();
        (storedUser.RetryCount.Value).ShouldBe(7);
        (storedUser.Label.Value).ShouldBe("user label");
        (storedUser.Database.Value!.Host.IsPresent).ShouldBeFalse();
        (storedUser.Database.Value.Port.Value).ShouldBe(7443);
        (resolved.Database!.Host).ShouldBe("defaults.db");
        (resolved.Database.Port).ShouldBe(7443);
    }

    [Test]
    public async Task EditSession_RoutesNestedChangesToTheMostSpecificSources()
    {
        var user = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment());
        var database = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment { Port = Optional<int>.Present(6432) }
                ),
            }
        );
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(3),
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment
                    {
                        Host = Optional<string>.Present("defaults.db"),
                        Port = Optional<int>.Present(5432),
                    }
                ),
            }
        );
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("user", user, priority: 100, writer: user),
                new("database", database, priority: 50, writer: database),
                new("defaults", defaults),
            ])
        );
        var writePlan = new StateWritePlan(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Database"] = "database",
                ["Database.Port"] = "user",
            }
        );

        using var session = await options.OpenEditSessionAsync(writePlan);
        session.Value.RetryCount = 7;
        session.Value.Database!.Host = "session.db";
        session.Value.Database.Port = 7443;
        var result = await session.CommitAsync();

        var userFragment = (await user.ReadAsync()).Value!;
        var databaseFragment = (await database.ReadAsync()).Value!;
        var resolved = (await options.ReadAsync()).Value!;
        (userFragment.RetryCount.Value).ShouldBe(7);
        (userFragment.Database.Value!.Host.IsPresent).ShouldBeFalse();
        (userFragment.Database.Value.Port.Value).ShouldBe(7443);
        (databaseFragment.Database.Value!.Host.Value).ShouldBe("session.db");
        (databaseFragment.Database.Value.Port.Value).ShouldBe(6432);
        (resolved.RetryCount).ShouldBe(7);
        (resolved.Database!.Host).ShouldBe("session.db");
        (resolved.Database.Port).ShouldBe(7443);
        (result.MultiWriteResult).ShouldNotBeNull();
        (result.MultiWriteResult!.PhysicalWriteCount).ShouldBe(2);
        ((result.MultiWriteResult.Sources.Select(static source => source.SourceId)))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "user", "database" }).OrderBy(static item => item));
    }

    [Test]
    public async Task EditSession_PathPlanRejectsEditsHiddenByAHigherPrioritySource()
    {
        var policy = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { Enabled = Optional<bool>.Present(true) }
        );
        var user = new InMemoryStateStore<AppSettings.Fragment>(new AppSettings.Fragment());
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("policy", policy, priority: 100),
                new("user", user, priority: 0, writer: user),
            ])
        );
        var userBefore = await user.ReadAsync();
        var writePlan = new StateWritePlan(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Enabled"] = "user" }
        );

        using var session = await options.OpenEditSessionAsync(writePlan);
        session.Value.Enabled = false;
        StateConflictException? rejection = null;
        try
        {
            await session.CommitAsync();
        }
        catch (StateConflictException exception)
        {
            rejection = exception;
        }

        var userAfter = await user.ReadAsync();
        rejection.ShouldNotBeNull();
        rejection.Message.ShouldContain("Enabled");
        rejection.Message.ShouldContain("policy");
        (userAfter.Value!.Enabled.IsPresent).ShouldBeFalse();
        (userAfter.Revision).ShouldBe(userBefore.Revision);
    }

    [Test]
    public async Task EditSession_DoesNotWriteWhenTheModelWasNotChanged()
    {
        var store = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new("user", store, writer: store)])
        );

        using var session = await options.OpenEditSessionAsync();
        var result = await session.CommitAsync();

        (session.IsCommitted).ShouldBeTrue();
        (result.Revision).ShouldBe("1");
        ((await store.ReadAsync()).Revision).ShouldBe("1");
    }

    [Test]
    public async Task EditSession_RebasesAppendEditsOntoTheSelectedSourceSegment()
    {
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { Plugins = Optional<IReadOnlyList<string>>.Present(["base"]) }
        );
        var user = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { Plugins = Optional<IReadOnlyList<string>>.Present(["user"]) }
        );
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("user", user, priority: 100, writer: user),
                new("defaults", defaults),
            ])
        );

        using var session = await options.OpenEditSessionAsync();
        session.Value.Plugins = [.. session.Value.Plugins, "session"];
        await session.CommitAsync();

        var storedUser = (await user.ReadAsync()).Value!;
        var resolved = (await options.ReadAsync()).Value!;
        ((storedUser.Plugins.Value!))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "user", "session" }).OrderBy(static item => item));
        ((resolved.Plugins))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "base", "user", "session" }).OrderBy(static item => item));
    }

    [Test]
    public async Task EditSession_RebasesConcurrentAppendAdditions()
    {
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { Plugins = Optional<IReadOnlyList<string>>.Present(["base"]) }
        );
        var user = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { Plugins = Optional<IReadOnlyList<string>>.Present(["user"]) }
        );
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("user", user, priority: 100, writer: user),
                new("defaults", defaults),
            ])
        );
        using var session = await options.OpenEditSessionAsync();
        session.Value.Plugins = [.. session.Value.Plugins, "session"];
        user.Set(
            new AppSettings.Fragment
            {
                Plugins = Optional<IReadOnlyList<string>>.Present(["user", "external"]),
            }
        );

        await session.CommitAsync();

        var resolved = (await options.ReadAsync()).Value!;
        resolved
            .Plugins.OrderBy(static value => value)
            .ShouldBe(
                (new[] { "base", "user", "external", "session" }).OrderBy(static value => value)
            );
    }

    [Test]
    public async Task EditSession_RebasesSetUnionEditsAndRejectsRemovingOtherSourceValues()
    {
        var defaults = new InMemoryStateStore<SetUnionSettings.Fragment>(
            new SetUnionSettings.Fragment
            {
                Tags = Optional<IReadOnlyList<string>>.Present(["base", "shared"]),
            }
        );
        var user = new InMemoryStateStore<SetUnionSettings.Fragment>(
            new SetUnionSettings.Fragment
            {
                Tags = Optional<IReadOnlyList<string>>.Present(["user", "shared"]),
            }
        );
        var options = new ConfiglueOptions<SetUnionSettings, SetUnionSettings.Fragment>(
            new StateSourceSet<SetUnionSettings.Fragment>([
                new("user", user, priority: 100, writer: user),
                new("defaults", defaults),
            ])
        );

        using var session = await options.OpenEditSessionAsync();
        session.Value.Tags = [.. session.Value.Tags, "session"];
        await session.CommitAsync();

        var storedUser = (await user.ReadAsync()).Value!;
        var resolved = (await options.ReadAsync()).Value!;
        ((storedUser.Tags.Value!))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "user", "session" }).OrderBy(static item => item));
        ((resolved.Tags))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "base", "shared", "user", "session" }).OrderBy(static item => item));

        using var removeLower = await options.OpenEditSessionAsync();
        removeLower.Value.Tags = removeLower
            .Value.Tags.Where(static tag => tag != "base")
            .ToArray();
        var rejected = false;
        try
        {
            await removeLower.CommitAsync();
        }
        catch (StateConflictException)
        {
            rejected = true;
        }

        (rejected).ShouldBeTrue();
        ((await user.ReadAsync()).Revision).ShouldBe("2");
    }

    [Test]
    public async Task EditSession_RebasesConcurrentSetUnionAdditions()
    {
        var defaults = new InMemoryStateStore<SetUnionSettings.Fragment>(
            new SetUnionSettings.Fragment
            {
                Tags = Optional<IReadOnlyList<string>>.Present(["base"]),
            }
        );
        var user = new InMemoryStateStore<SetUnionSettings.Fragment>(
            new SetUnionSettings.Fragment
            {
                Tags = Optional<IReadOnlyList<string>>.Present(["user"]),
            }
        );
        var options = new ConfiglueOptions<SetUnionSettings, SetUnionSettings.Fragment>(
            new StateSourceSet<SetUnionSettings.Fragment>([
                new("user", user, priority: 100, writer: user),
                new("defaults", defaults),
            ])
        );
        using var session = await options.OpenEditSessionAsync();
        session.Value.Tags = [.. session.Value.Tags, "session"];
        user.Set(
            new SetUnionSettings.Fragment
            {
                Tags = Optional<IReadOnlyList<string>>.Present(["user", "external"]),
            }
        );

        await session.CommitAsync();

        var resolved = (await options.ReadAsync()).Value!;
        resolved
            .Tags.OrderBy(static value => value)
            .ShouldBe(
                (new[] { "base", "user", "external", "session" }).OrderBy(static value => value)
            );
    }

    [Test]
    public async Task EditSession_RejectsAWriteHiddenByHigherPriorityReadOnlySource()
    {
        var policy = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { Enabled = Optional<bool>.Present(true) }
        );
        var user = new InMemoryStateStore<AppSettings.Fragment>();
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("policy", policy, priority: 100),
                new("user", user, priority: 0, writer: user),
            ])
        );

        using var session = await options.OpenEditSessionAsync();
        session.Value.Enabled = false;
        var rejected = false;
        try
        {
            await session.CommitAsync();
        }
        catch (StateConflictException)
        {
            rejected = true;
        }

        (rejected).ShouldBeTrue();
        ((await user.ReadAsync()).Status).ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public async Task Options_MigratesEachSourceFragmentBeforeMerging()
    {
        var oldSchema = new StateSchemaMetadata("app-settings", 1);
        var reader = new FixedStateReader<AppSettings.Fragment>(
            StateReadResult<AppSettings.Fragment>.Success(
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(9) },
                "revision-1",
                oldSchema
            )
        );
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([new("legacy", reader)]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            sourceSet,
            migrations: [new AppSettingsV1ToV2Migration()]
        );

        var result = await options.ReadAsync();

        (result.Status).ShouldBe(StateReadStatus.Success);
        (result.Value!.RetryCount).ShouldBe(9);
        (result.Value.Label).ShouldBe("migrated");
        (result.Schema).ShouldBe(AppSettings.ConfiglueSchema.ToMetadata());
    }

    [Test]
    public async Task Options_ValidatesBeforeSavingAndLeavesTheStoredRevisionUntouched()
    {
        var store = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([
            new("user", store, writer: store),
        ]);
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueValidator<AppSettings>(new RetryCountValidator());
        services.AddConfiglueOptions<AppSettings, AppSettings.Fragment>(
            sourceSet,
            validateDataAnnotations: true
        );
        using var serviceProvider = services.BuildServiceProvider();
        var writable = serviceProvider.GetRequiredService<IWritableOptions<AppSettings>>();
        ConfiglueValidationException? validationFailure = null;

        try
        {
            await writable.SaveAsync(patch => patch.RetryCount = 101);
        }
        catch (ConfiglueValidationException exception)
        {
            validationFailure = exception;
        }

        var stored = await store.ReadAsync();
        (validationFailure).ShouldNotBeNull();
        (validationFailure!.Failures.Count()).ShouldBe(2);
        (
            validationFailure.Failures.Any(failure =>
                failure.Contains("custom retry limit", StringComparison.Ordinal)
            )
        ).ShouldBeTrue();
        (stored.Value!.RetryCount.Value).ShouldBe(3);
        (stored.Revision).ShouldBe("1");

        var advanced = serviceProvider.GetRequiredService<IConfiglueOptions<AppSettings>>();
        using var edit = await advanced.OpenEditSessionAsync();
        edit.Value.RetryCount = 101;
        var editWasRejected = false;
        try
        {
            await edit.CommitAsync();
        }
        catch (ConfiglueValidationException)
        {
            editWasRejected = true;
        }

        edit.Value.RetryCount = 4;
        await edit.CommitAsync();
        var validStored = await store.ReadAsync();
        (editWasRejected).ShouldBeTrue();
        (edit.IsCommitted).ShouldBeTrue();
        (validStored.Value!.RetryCount.Value).ShouldBe(4);
    }

    [Test]
    public async Task NamedAndRuntimeProfiles_PassTheirNamesToMicrosoftValidators()
    {
        var defaultStore = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var keyedStore = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var runtimeStores = new Dictionary<string, InMemoryStateStore<AppSettings.Fragment>>(
            StringComparer.Ordinal
        );
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueValidator(new ProfileScopedRetryCountValidator());
        services.AddConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("default", defaultStore, writer: defaultStore),
            ])
        );
        services.AddConfiglueOptions<AppSettings, AppSettings.Fragment>(
            "custom",
            new StateSourceSet<AppSettings.Fragment>([
                new("custom", keyedStore, writer: keyedStore),
            ])
        );
        services.AddConfiglueOptionsRegistry<AppSettings, AppSettings.Fragment>(
            (_, profileName) =>
            {
                var store = new InMemoryStateStore<AppSettings.Fragment>(
                    new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
                );
                runtimeStores.Add(profileName, store);
                return new StateSourceSet<AppSettings.Fragment>([
                    new(profileName, store, writer: store),
                ]);
            }
        );
        using var serviceProvider = services.BuildServiceProvider();

        var defaultOptions = serviceProvider.GetRequiredService<IWritableOptions<AppSettings>>();
        await defaultOptions.SaveAsync(patch => patch.RetryCount = 12);
        ((await defaultStore.ReadAsync()).Value!.RetryCount.Value).ShouldBe(12);

        var keyedOptions = serviceProvider.GetRequiredKeyedService<IWritableOptions<AppSettings>>(
            "custom"
        );
        var keyedBefore = await keyedStore.ReadAsync();
        var keyedFailure = await SaveInvalidAndCaptureAsync(keyedOptions);
        var keyedAfter = await keyedStore.ReadAsync();

        (keyedFailure.OptionsName).ShouldBe("custom");
        (keyedFailure.Failures).ShouldContain("RetryCount is too high for this profile.");
        (keyedAfter.Revision).ShouldBe(keyedBefore.Revision);
        (keyedAfter.Value!.RetryCount.Value).ShouldBe(3);

        var registry = serviceProvider.GetRequiredService<IConfiglueOptionsRegistry<AppSettings>>();
        (registry.TryAdd("runtime")).ShouldBeTrue();
        var runtimeOptions = registry.Get("runtime");
        var runtimeBefore = await runtimeStores["runtime"].ReadAsync();
        var runtimeFailure = await SaveInvalidAndCaptureAsync(runtimeOptions);
        var runtimeAfter = await runtimeStores["runtime"].ReadAsync();

        (runtimeFailure.OptionsName).ShouldBe("runtime");
        (runtimeFailure.Failures).ShouldContain("RetryCount is too high for this profile.");
        (runtimeAfter.Revision).ShouldBe(runtimeBefore.Revision);
        (runtimeAfter.Value!.RetryCount.Value).ShouldBe(3);
    }

    [Test]
    public async Task SaveAsync_ChangesOnlyTheTargetContributionAndUnsetRevealsLowerValues()
    {
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(3),
                Label = Optional<string?>.Present("default label"),
            }
        );
        var user = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Enabled = Optional<bool>.Present(false),
                RetryCount = Optional<int>.Present(9),
                Label = Optional<string?>.Present("old label"),
            }
        );
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([
            new("user", user, priority: 100, writer: user),
            new("defaults", defaults, priority: 0),
        ]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sourceSet);
        var patch = new AppSettings.Patch
        {
            RetryCount = FragmentOperation<int>.Unset,
            Label = FragmentOperation<string?>.Set("patched label"),
        };

        var write = await options.SaveAsync(patch);
        var stored = await user.ReadAsync();
        var resolved = await options.ReadAsync();

        (write.Revision).ShouldBe("2");
        (stored.Value!.Enabled.IsPresent).ShouldBeTrue();
        (stored.Value.RetryCount.IsPresent).ShouldBeFalse();
        (stored.Value.Label.Value).ShouldBe("patched label");
        (resolved.Value!.Enabled).ShouldBeFalse();
        (resolved.Value.RetryCount).ShouldBe(3);
        (resolved.Value.Label).ShouldBe("patched label");
    }

    [Test]
    public async Task SaveAsync_UnsetRevealsTheModelDefaultsContribution()
    {
        var store = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(12) }
        );
        await using var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new("user", store, writer: store)])
        );

        await options.SaveAsync(
            new AppSettings.Patch { RetryCount = FragmentOperation<int>.Unset }
        );
        var resolved = await options.ReadAsync();
        var details = await options.GetDetailsAsync();

        (resolved.Value!.RetryCount).ShouldBe(3);
        (details.RetryCount.Source?.Kind).ShouldBe("model-defaults");
    }

    [Test]
    public async Task DependencyInjection_ResolvesNamedProfilesByServiceKey()
    {
        var primaryStore = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(5) }
        );
        var secondaryStore = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) }
        );
        var primarySources = new StateSourceSet<AppSettings.Fragment>([
            new("profile", primaryStore),
        ]);
        var secondarySources = new StateSourceSet<AppSettings.Fragment>([
            new("profile", secondaryStore),
        ]);
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueOptions<AppSettings, AppSettings.Fragment>("primary", primarySources);
        services.AddConfiglueOptions<AppSettings, AppSettings.Fragment>(
            "secondary",
            secondarySources
        );
        using var serviceProvider = services.BuildServiceProvider();
        var primary = serviceProvider.GetRequiredKeyedService<IReadOnlyOptions<AppSettings>>(
            "primary"
        );
        var secondary = serviceProvider.GetRequiredKeyedService<IReadOnlyOptions<AppSettings>>(
            "secondary"
        );

        var primaryValue = await primary.GetValueAsync();
        var secondaryValue = await secondary.GetValueAsync();

        (primaryValue.RetryCount).ShouldBe(5);
        (secondaryValue.RetryCount).ShouldBe(8);
    }

    [Test]
    public async Task DependencyInjection_ManagesDynamicProfilesThroughRegistry()
    {
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        var factoryCalls = 0;
        services.AddConfiglueOptionsRegistry<AppSettings, AppSettings.Fragment>(
            (_, profileName) =>
            {
                Interlocked.Increment(ref factoryCalls);
                var store = new InMemoryStateStore<AppSettings.Fragment>(
                    new AppSettings.Fragment
                    {
                        RetryCount = Optional<int>.Present(profileName == "primary" ? 5 : 8),
                    }
                );
                return new StateSourceSet<AppSettings.Fragment>([new("profile", store)]);
            }
        );
        using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<IConfiglueOptionsRegistry<AppSettings>>();
        var added = new List<string>();
        var removed = new List<string>();
        registry.ProfileAdded += (name, _) => added.Add(name);
        registry.ProfileRemoved += name => removed.Add(name);

        var addResults = new bool[16];
        Parallel.For(0, addResults.Length, index => addResults[index] = registry.TryAdd("primary"));
        registry.TryAdd("secondary");
        var primary = await registry.Get("primary").GetValueAsync();
        var secondary = await registry.Get("secondary").GetValueAsync();
        var removedPrimary = registry.TryRemove("primary");
        var removedAgain = registry.TryRemove("primary");

        (addResults.Count(static result => result)).ShouldBe(1);
        (factoryCalls).ShouldBe(2);
        (primary.RetryCount).ShouldBe(5);
        (secondary.RetryCount).ShouldBe(8);
        ((registry.ProfileNames))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "secondary" }).OrderBy(static item => item));
        ((added))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "primary", "secondary" }).OrderBy(static item => item));
        ((removed))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "primary" }).OrderBy(static item => item));
        (removedPrimary).ShouldBeTrue();
        (removedAgain).ShouldBeFalse();
    }

    [Test]
    public async Task RegistryAddClearAndDisposeWaitForQueuedNotifications()
    {
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueOptionsRegistry<AppSettings, AppSettings.Fragment>(
            (_, name) =>
            {
                var store = new InMemoryStateStore<AppSettings.Fragment>();
                return new StateSourceSet<AppSettings.Fragment>([
                    new(name, store, writer: store, watcher: store),
                ]);
            }
        );
        using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<IConfiglueOptionsRegistry<AppSettings>>();
        var firstAdded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFirstAdded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var addOrder = new List<string>();
        registry.ProfileAdded += (name, _) =>
        {
            if (name == "first")
            {
                firstAdded.TrySetResult();
                releaseFirstAdded.Task.GetAwaiter().GetResult();
            }
            addOrder.Add(name);
        };

        var firstAdd = Task.Run(() => registry.TryAdd("first"));
        await firstAdded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondAdd = Task.Run(() => registry.TryAdd("second"));
        try
        {
            (
                await Task.Run(() =>
                    SpinWait.SpinUntil(
                        () => registry.TryGet("second", out _),
                        TimeSpan.FromSeconds(5)
                    )
                )
            ).ShouldBeTrue();
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            (secondAdd.IsCompleted).ShouldBeFalse();
        }
        finally
        {
            releaseFirstAdded.TrySetResult();
        }

        (await Task.WhenAll(firstAdd, secondAdd)).ShouldBe(new[] { true, true });
        (addOrder).ShouldBe(new[] { "first", "second" });

        var firstRemoved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFirstRemoved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var removedNames = new List<string>();
        registry.ProfileRemoved += name =>
        {
            if (name == "first")
            {
                firstRemoved.TrySetResult();
                releaseFirstRemoved.Task.GetAwaiter().GetResult();
            }
            removedNames.Add(name);
        };

        var removeFirst = Task.Run(() => registry.TryRemove("first"));
        await firstRemoved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var clear = Task.Run(async () => await registry.ClearAsync());
        try
        {
            (
                await Task.Run(() =>
                    SpinWait.SpinUntil(
                        () => registry.ProfileNames.Count == 0,
                        TimeSpan.FromSeconds(5)
                    )
                )
            ).ShouldBeTrue();
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            (clear.IsCompleted).ShouldBeFalse();
        }
        finally
        {
            releaseFirstRemoved.TrySetResult();
        }

        await Task.WhenAll(removeFirst, clear);
        (removedNames).ShouldBe(new[] { "first", "second" });

        registry.TryAdd("dispose").ShouldBeTrue();
        var disposedNameRemoved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseDisposeNotification = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        registry.ProfileRemoved += name =>
        {
            if (name == "dispose")
            {
                disposedNameRemoved.TrySetResult();
                releaseDisposeNotification.Task.GetAwaiter().GetResult();
            }
        };

        var dispose = Task.Run(async () => await registry.DisposeAsync());
        try
        {
            await disposedNameRemoved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            (dispose.IsCompleted).ShouldBeFalse();
        }
        finally
        {
            releaseDisposeNotification.TrySetResult();
        }

        await dispose;
    }

    [Test]
    public async Task RegistryNotificationsCanReenterClearAndDispose()
    {
        var services = new ServiceCollection();
        services.AddConfiglueMicrosoftOptions<AppSettings>();
        services.AddConfiglueOptionsRegistry<AppSettings, AppSettings.Fragment>(
            (_, name) =>
            {
                var store = new InMemoryStateStore<AppSettings.Fragment>();
                return new StateSourceSet<AppSettings.Fragment>([
                    new(name, store, writer: store, watcher: store),
                ]);
            }
        );
        using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<IConfiglueOptionsRegistry<AppSettings>>();
        registry.TryAdd("clear").ShouldBeTrue();
        registry.ProfileRemoved += name =>
        {
            if (name == "clear")
            {
                registry.Clear();
            }
        };

        (
            await Task.Run(() => registry.TryRemove("clear")).WaitAsync(TimeSpan.FromSeconds(5))
        ).ShouldBeTrue();

        var listenerAfterFailureWasCalled = false;
        registry.ProfileAdded += (name, _) =>
        {
            if (name == "listener-error")
            {
                throw new InvalidOperationException("listener failure");
            }
        };
        registry.ProfileAdded += (name, _) =>
        {
            if (name == "listener-error")
            {
                listenerAfterFailureWasCalled = true;
            }
        };
        registry.TryAdd("listener-error").ShouldBeTrue();
        (listenerAfterFailureWasCalled).ShouldBeTrue();
        registry.TryRemove("listener-error").ShouldBeTrue();

        registry.TryAdd("dispose").ShouldBeTrue();
        registry.ProfileRemoved += name =>
        {
            if (name == "dispose")
            {
                registry.Dispose();
            }
        };

        (
            await Task.Run(() => registry.TryRemove("dispose")).WaitAsync(TimeSpan.FromSeconds(5))
        ).ShouldBeTrue();
    }

    [Test]
    public async Task SourceProjection_MapsNestedSourceContractsAndRoutesWritesBack()
    {
        var remoteDatabase = new InMemoryStateStore<DatabaseSettings.Fragment>(
            new DatabaseSettings.Fragment { Host = Optional<string>.Present("remote.db") }
        );
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(
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
        var databaseSource = new StateSource<DatabaseSettings.Fragment>(
            "remote-database",
            remoteDatabase,
            priority: 100,
            writer: remoteDatabase,
            physicalOrigin: "database-row"
        );
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
            new("defaults", defaults, priority: 0),
        ]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            sourceSet,
            StateWriteRoute.To("remote-database")
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
        var source = new StateSource<DatabaseSettings.Fragment>(
            "legacy-database",
            legacy,
            priority: 100
        );
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
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sources);

        var resolved = await options.ReadAsync();

        (resolved.Value!.Database!.Host).ShouldBe("legacy.db");
        (resolved.Value.Database.Port).ShouldBe(7400);
        (resolved.Schema).ShouldBe(AppSettings.ConfiglueSchema.ToMetadata());
    }

    [Test]
    public async Task Options_ExplainsEffectiveNestedValuesAndSparseSourceContributions()
    {
        var user = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment { Port = Optional<int>.Present(6432) }
                ),
            }
        );
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(
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
            new("user", user, priority: 100, physicalOrigin: "user-settings.json"),
            new("defaults", defaults, priority: 0, physicalOrigin: "defaults.json"),
        ]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sourceSet);

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

    [Test]
    public async Task Options_ExplainsAppendElementOriginsIncludingDuplicates()
    {
        var user = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Plugins = Optional<IReadOnlyList<string>>.Present(["user", "shared"]),
            }
        );
        var defaults = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Plugins = Optional<IReadOnlyList<string>>.Present(["base", "shared"]),
            }
        );
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("user", user, priority: 100),
                new("defaults", defaults, priority: 0),
            ])
        );

        var details = await options.GetDetailsAsync();
        var plugins = details.Plugins!;

        (plugins.Value).ShouldBe(["base", "shared", "user", "shared"]);
        plugins.Elements.Count.ShouldBe(4);
        ShouldHaveElementSources(plugins, 0, "base", 1);
        ShouldHaveElementSources(plugins, 1, "shared", 1);
        ShouldHaveElementSources(plugins, 2, "user", 0);
        ShouldHaveElementSources(plugins, 3, "shared", 0);
    }

    [Test]
    public async Task Options_ExplainsSetUnionElementOriginsForOverlappingValues()
    {
        var user = new InMemoryStateStore<SetUnionSettings.Fragment>(
            new SetUnionSettings.Fragment
            {
                Tags = Optional<IReadOnlyList<string>>.Present(["user", "shared"]),
            }
        );
        var defaults = new InMemoryStateStore<SetUnionSettings.Fragment>(
            new SetUnionSettings.Fragment
            {
                Tags = Optional<IReadOnlyList<string>>.Present(["base", "shared"]),
            }
        );
        var options = new ConfiglueOptions<SetUnionSettings, SetUnionSettings.Fragment>(
            new StateSourceSet<SetUnionSettings.Fragment>([
                new("user", user, priority: 100),
                new("defaults", defaults, priority: 0),
            ])
        );

        var details = await options.GetDetailsAsync();
        var tags = details.Tags!;

        (tags.Value).ShouldBe(["base", "shared", "user"]);
        tags.Elements.Count.ShouldBe(3);
        ShouldHaveElementSources(tags, 0, "base", 1);
        ShouldHaveElementSources(tags, 1, "shared", 0, 1);
        ShouldHaveElementSources(tags, 2, "user", 0);
    }

    [Test]
    public async Task Options_ExplainsOnlyTheWinningSourceForReplacementCollectionElements()
    {
        var user = new InMemoryStateStore<ReplaceCollectionSettings.Fragment>(
            new ReplaceCollectionSettings.Fragment
            {
                Values = Optional<IReadOnlyList<string>>.Present(["user"]),
            }
        );
        var defaults = new InMemoryStateStore<ReplaceCollectionSettings.Fragment>(
            new ReplaceCollectionSettings.Fragment
            {
                Values = Optional<IReadOnlyList<string>>.Present(["default"]),
            }
        );
        var options = new ConfiglueOptions<
            ReplaceCollectionSettings,
            ReplaceCollectionSettings.Fragment
        >(
            new StateSourceSet<ReplaceCollectionSettings.Fragment>([
                new("user", user, priority: 100),
                new("defaults", defaults, priority: 0),
            ])
        );

        var details = await options.GetDetailsAsync();
        var values = details.Values!;

        values.Elements.Count.ShouldBe(1);
        ShouldHaveElementSources(values, 0, "user", 0);
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
        var target = new InMemoryStateStore<AppSettings.Fragment>();
        var sources = new StateSourceSet<AppSettings.Fragment>([
            new("environment", environment, priority: 100),
            new("legacy", legacy, priority: 50),
            new("current", target, priority: 0, writer: target),
        ]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            sources,
            migrations: [new AppSettingsV1ToV2Migration()]
        );

        var migration = await options.MigrateSourceAsync("legacy", "current");
        var copied = await target.ReadAsync();

        (migration.SourceId).ShouldBe("legacy");
        (migration.TargetId).ShouldBe("current");
        (migration.SourceRevision).ShouldBe("legacy-revision");
        (migration.TargetRevision).ShouldBe("1");
        (copied.Value!.RetryCount.Value).ShouldBe(9);
        (copied.Value.Label.Value).ShouldBe("migrated");
        (copied.Value.Label.Value).ShouldNotBe("environment-value");
    }

    [Test]
    public async Task MigrateSourcesToTargetsAsync_MergesSelectedSourcesAndSkipsCompletedTargetsOnRetry()
    {
        var environment = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("environment-value") }
        );
        var user = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment { Port = Optional<int>.Present(6432) }
                ),
                Plugins = Optional<IReadOnlyList<string>>.Present(["user-plugin"]),
            }
        );
        var legacy = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(12),
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment { Host = Optional<string>.Present("legacy.db") }
                ),
            }
        );
        var primaryTarget = new InMemoryStateStore<AppSettings.Fragment>();
        var retryTarget = new InMemoryStateStore<AppSettings.Fragment>();
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("environment", environment, priority: 200),
                new("user", user, priority: 100),
                new("legacy", legacy, priority: 50),
                new("primary", primaryTarget, priority: 0, writer: primaryTarget),
                new("retry-only", retryTarget, priority: -1, writer: retryTarget),
            ])
        );
        var targets = new Dictionary<string, Func<IConfiglueFragment, IConfiglueFragment>>(
            StringComparer.Ordinal
        )
        {
            ["primary"] = static fragment => fragment,
            ["retry-only"] = static fragment => new AppSettings.Fragment
            {
                RetryCount = ((AppSettings.Fragment)fragment).RetryCount,
            },
        };
        IConfiglueOptions<AppSettings> writableOptions = options;

        var firstRun = await writableOptions.MigrateSourcesToTargetsAsync(
            ["legacy", "user"],
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
            ["legacy", "user"],
            targets
        );

        (secondRun.Targets.All(static result => result.WasAlreadyCurrent)).ShouldBeTrue();
        (secondRun.Targets.Count).ShouldBe(2);
        (secondRun.Targets.All(static result => result.TargetRevision == "1")).ShouldBeTrue();
        ((secondRun.SourceIds))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "user", "legacy" }).OrderBy(static item => item));
    }

    [Test]
    public async Task MigrateSourcesToTargetsAsync_ResumesAfterALaterTargetFails()
    {
        var source = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(22) }
        );
        var firstTarget = new InMemoryStateStore<AppSettings.Fragment>();
        var secondTarget = new InMemoryStateStore<AppSettings.Fragment>();
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("source", source, priority: 100, writer: source),
                new("first-target", firstTarget, priority: 0, writer: firstTarget),
                new(
                    "second-target",
                    secondTarget,
                    priority: -1,
                    writer: new FailOnceStateWriter<AppSettings.Fragment>(secondTarget)
                ),
            ])
        );
        var targets = new Dictionary<string, Func<IConfiglueFragment, IConfiglueFragment>>(
            StringComparer.Ordinal
        )
        {
            ["first-target"] = static fragment => fragment,
            ["second-target"] = static fragment => fragment,
        };
        IConfiglueOptions<AppSettings> writableOptions = options;
        var failed = false;
        try
        {
            await writableOptions.MigrateSourcesToTargetsAsync(
                ["source"],
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
        (afterFailure.Revisions!.TryGetRevision("source", out _)).ShouldBeTrue();
        (afterFailure.Value!.RetryCount).ShouldBe(22);

        var resumed = await writableOptions.MigrateSourcesToTargetsAsync(
            ["source"],
            targets,
            retireSources: true
        );

        (resumed.Targets[0].WasAlreadyCurrent).ShouldBeTrue();
        (resumed.Targets[1].WasAlreadyCurrent).ShouldBeFalse();
        (resumed.SourcesRetired).ShouldBeTrue();
        ((resumed.RetiredSourceIds))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "source" }).OrderBy(static item => item));
        ((await secondTarget.ReadAsync()).Value!.RetryCount.Value).ShouldBe(22);

        var repeated = await writableOptions.MigrateSourcesToTargetsAsync(
            ["source"],
            targets,
            retireSources: true
        );
        (repeated.Targets.All(static target => target.WasAlreadyCurrent)).ShouldBeTrue();
        (repeated.SourcesRetired).ShouldBeTrue();
        ((await options.ReadAsync()).Value!.RetryCount).ShouldBe(22);
        ((await options.ReadAsync()).Revisions!.TryGetRevision("source", out _)).ShouldBeFalse();

        await options.SaveAsync(settings => settings.RetryCount = 23);
        ((await source.ReadAsync()).Value!.RetryCount.Value).ShouldBe(22);
        ((await firstTarget.ReadAsync()).Value!.RetryCount.Value).ShouldBe(23);
        ((await options.ReadAsync()).Value!.RetryCount).ShouldBe(23);
    }

    [Test]
    public async Task MigrateSourcesToTargetsAsync_RefusesRetirementThatWouldChangeEffectiveModel()
    {
        var source = new InMemoryStateStore<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(22) }
        );
        var target = new InMemoryStateStore<AppSettings.Fragment>();
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new("source", source, priority: 100),
                new("target", target, priority: 0, writer: target),
            ])
        );
        IConfiglueOptions<AppSettings> writableOptions = options;
        var projections = new Dictionary<string, Func<IConfiglueFragment, IConfiglueFragment>>(
            StringComparer.Ordinal
        )
        {
            ["target"] = static _ => new AppSettings.Fragment
            {
                RetryCount = Optional<int>.Present(25),
            },
        };
        var rejected = false;
        try
        {
            await writableOptions.MigrateSourcesToTargetsAsync(
                ["source"],
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
        (resolved.Revisions!.TryGetRevision("source", out _)).ShouldBeTrue();
        (resolved.Value!.RetryCount).ShouldBe(22);
        ((await target.ReadAsync()).Value!.RetryCount.Value).ShouldBe(25);
    }

    [Test]
    public async Task SerializedStateSource_ComposesResourceCodecWriterAndWatcherCapabilities()
    {
        var resource = new InMemoryResource();
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "serialized",
            resource,
            new JsonStateCodec<AppSettings.Fragment>(),
            physicalOrigin: "memory://settings"
        );
        var sourceSet = new StateSourceSet<AppSettings.Fragment>([source]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sourceSet);

        await options.SaveAsync(
            new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(12) }
        );
        var resolved = await options.ReadAsync();
        var storedResource = await resource.ReadAsync();

        (source.Writer).ShouldNotBeNull();
        (source.Watcher).ShouldNotBeNull();
        (resolved.Value!.RetryCount).ShouldBe(12);
        (resolved.PhysicalOrigin).ShouldBe("memory://settings");
        (storedResource.Schema).ShouldBe(AppSettings.ConfiglueSchema.ToMetadata());
    }

    private sealed class FixedStateReader<T>(StateReadResult<T> result) : IStateReader<T>
    {
        public ValueTask<StateReadResult<T>> ReadAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class OpaqueRevisionStateStore<T> : IStateReader<T>, IStateWatcher
    {
        private const string Revision = "opaque";
        private readonly object _gate = new();
        private StateReadResult<T> _result;
        private TaskCompletionSource _changed = NewSignal();
        private readonly TaskCompletionSource _watchStarted = NewSignal();

        public OpaqueRevisionStateStore(
            StateReadStatus status,
            T? value = default,
            string? physicalOrigin = null
        ) =>
            _result = new StateReadResult<T>(
                status,
                value,
                Revision,
                PhysicalOrigin: physicalOrigin
            );

        public Task WatchStarted => _watchStarted.Task;

        public ValueTask<StateReadResult<T>> ReadAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return ValueTask.FromResult(_result);
            }
        }

        public async ValueTask WaitForChangeAsync(
            string? observedRevision,
            CancellationToken cancellationToken = default
        )
        {
            Task waitTask;
            lock (_gate)
            {
                if (!string.Equals(observedRevision, Revision, StringComparison.Ordinal))
                {
                    return;
                }

                _watchStarted.TrySetResult();
                waitTask = _changed.Task;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public void SetSuccess(T value, string? physicalOrigin)
        {
            TaskCompletionSource changed;
            lock (_gate)
            {
                _result = new StateReadResult<T>(
                    StateReadStatus.Success,
                    value,
                    Revision,
                    PhysicalOrigin: physicalOrigin
                );
                changed = _changed;
                _changed = NewSignal();
            }

            changed.TrySetResult();
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PendingStateWatcher : IStateWatcher
    {
        private readonly TaskCompletionSource<bool> _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource<bool> _cancellationObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task<bool> Started => _started.Task;

        public Task<bool> CancellationObserved => _cancellationObserved.Task;

        public ValueTask WaitForChangeAsync(
            string? observedRevision,
            CancellationToken cancellationToken = default
        )
        {
            _started.TrySetResult(true);
            _ = cancellationToken.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                _cancellationObserved
            );
            return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        }
    }

    private sealed class SynchronousThrowingStateWatcher : IStateWatcher
    {
        public ValueTask WaitForChangeAsync(
            string? observedRevision,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Simulated synchronous watcher startup failure.");
    }

    private sealed class FailOnceStateWriter<T>(IStateWriter<T> inner) : IStateWriter<T>
    {
        private int _shouldFail = 1;

        public ValueTask<StateWriteResult> WriteAsync(
            StateWriteRequest<T> request,
            CancellationToken cancellationToken = default
        )
        {
            if (Interlocked.Exchange(ref _shouldFail, 0) == 1)
            {
                throw new IOException("Simulated transient target failure.");
            }

            return inner.WriteAsync(request, cancellationToken);
        }
    }

    private sealed class AppSettingsV1ToV2Migration : IStateSchemaMigration<AppSettings.Fragment>
    {
        public StateSchemaMetadata SourceSchema => new("app-settings", 1);

        public StateSchemaMetadata TargetSchema => new("app-settings", 2);

        public ValueTask<AppSettings.Fragment> MigrateAsync(
            AppSettings.Fragment value,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var builder = value.ToBuilder();
            builder.Label = Optional<string?>.Present("migrated");
            return ValueTask.FromResult(builder.Build());
        }
    }

    private sealed class DatabaseV1ToV2Migration : IStateSchemaMigration<DatabaseSettings.Fragment>
    {
        public StateSchemaMetadata SourceSchema => new("database-settings", 1);

        public StateSchemaMetadata TargetSchema => DatabaseSettings.ConfiglueSchema.ToMetadata();

        public ValueTask<DatabaseSettings.Fragment> MigrateAsync(
            DatabaseSettings.Fragment value,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var builder = value.ToBuilder();
            builder.Port = Optional<int>.Present(7400);
            return ValueTask.FromResult(builder.Build());
        }
    }

    private sealed class RetryCountValidator : IValidateOptions<AppSettings>
    {
        public ValidateOptionsResult Validate(string? name, AppSettings options) =>
            options.RetryCount > 10
                ? ValidateOptionsResult.Fail("RetryCount exceeds the custom retry limit.")
                : ValidateOptionsResult.Success;
    }

    private static void ShouldHaveElementSources(
        ConfigCollectionDetails<string> details,
        int index,
        object? value,
        params int[] sourceIndices
    )
    {
        var element = details.Elements[index];
        element.Index.ShouldBe(index);
        element.Value.ShouldBe(value);
        element
            .Contributions.Select(contribution => contribution.Source.Key)
            .ToArray()
            .ShouldBe(
                sourceIndices
                    .Select(sourceIndex => details.Sources[sourceIndex].Source.Key)
                    .ToArray()
            );
    }

    private sealed class ProfileScopedRetryCountValidator : IValidateOptions<AppSettings>
    {
        public ValidateOptionsResult Validate(string? name, AppSettings options) =>
            name is "custom" or "runtime" && options.RetryCount > 10
                ? ValidateOptionsResult.Fail("RetryCount is too high for this profile.")
                : ValidateOptionsResult.Success;
    }

    private static async Task<ConfiglueValidationException> SaveInvalidAndCaptureAsync(
        IWritableOptions<AppSettings> options
    )
    {
        try
        {
            await options.SaveAsync(patch => patch.RetryCount = 12);
        }
        catch (ConfiglueValidationException exception)
        {
            return exception;
        }

        throw new InvalidOperationException(
            "The profile-specific validator did not reject the value."
        );
    }
}
