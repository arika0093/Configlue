using System.Text.Json;
using Configlue.Hosting.Blazor;
using Configlue.Sources;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Configlue.Tests;

[ConfiglueModel("runtime-lifetime-settings", Version = 1)]
public partial class RuntimeLifetimeSettings
{
    public string? Label { get; set; }
}

public sealed partial class RuntimeLifetimeTests
{
    [Test]
    public async Task Shutdown_WaitsForCanceledSourceWatchersToReleaseResources()
    {
        var store = new InMemoryStateSource<RuntimeLifetimeSettings.Fragment>(
            new RuntimeLifetimeSettings.Fragment { Label = "initial" }
        );
        var immediate = new ShutdownWatcher(delayCleanup: false);
        var delayed = new ShutdownWatcher(delayCleanup: true);
        var runtime = new ConfiglueRuntime<
            RuntimeLifetimeSettings,
            RuntimeLifetimeSettings.Fragment
        >(
            new StateSourceSet<RuntimeLifetimeSettings.Fragment>([
                new("immediate", store, watcher: immediate),
                new("delayed", store, watcher: delayed),
            ])
        );
        using var subscription = runtime.OnChange(_ => { });
        await immediate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await delayed.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdown = runtime.DisposeAsync().AsTask();
        try
        {
            await delayed.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            shutdown.IsCompleted.ShouldBeFalse(
                "Shutdown must wait until every source watcher releases its resources."
            );
        }
        finally
        {
            delayed.Release.TrySetResult();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task ShutdownWaitsForManuallyDisposedSubjectWatcherCleanup()
    {
        var store = new InMemoryStateSource<RuntimeLifetimeSettings.Fragment>(
            new RuntimeLifetimeSettings.Fragment { Label = "initial" }
        );
        var delayed = new ShutdownWatcher(delayCleanup: true);
        var runtime = new ConfiglueRuntime<
            RuntimeLifetimeSettings,
            RuntimeLifetimeSettings.Fragment
        >(
            new StateSourceSet<RuntimeLifetimeSettings.Fragment>([
                new("delayed", store, watcher: delayed),
            ]),
            onChangeDebounce: TimeSpan.Zero
        );
        ISubjectState<RuntimeLifetimeSettings> subjectState = runtime;
        var subscription = subjectState.ForSubject(new LifetimeSubject()).OnChange(_ => { });
        await delayed.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        subscription.Dispose();
        await delayed.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdown = runtime.DisposeAsync().AsTask();
        try
        {
            shutdown.IsCompleted.ShouldBeFalse(
                "Shutdown must drain a subject watcher whose subscription was already disposed."
            );
        }
        finally
        {
            delayed.Release.TrySetResult();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ShutdownWaitsForSubjectWatcherLifetimeCleanupAfterWatchLoopCompletes(
        bool cleanupFails
    )
    {
        var store = new InMemoryStateSource<RuntimeLifetimeSettings.Fragment>(
            new RuntimeLifetimeSettings.Fragment { Label = "initial" }
        );
        var watcher = new ShutdownWatcher(delayCleanup: false);
        var runtime = new ConfiglueRuntime<
            RuntimeLifetimeSettings,
            RuntimeLifetimeSettings.Fragment
        >(
            new StateSourceSet<RuntimeLifetimeSettings.Fragment>([
                new("watcher", store, watcher: watcher),
            ]),
            onChangeDebounce: TimeSpan.Zero
        );
        var cleanupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var cleanupRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        runtime.WatcherCleanupBarrier = async () =>
        {
            cleanupEntered.TrySetResult();
            await cleanupRelease.Task;
            if (cleanupFails)
                throw new InvalidOperationException("Watcher cleanup failed.");
        };

        ISubjectState<RuntimeLifetimeSettings> subjectState = runtime;
        subjectState.ForSubject(new LifetimeSubject()).OnChange(_ => { });
        await watcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var shutdown = runtime.DisposeAsync().AsTask();
        try
        {
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The watch loop returned before cleanup; only the final lifetime cleanup is blocked.
            watcher.Canceled.Task.IsCompleted.ShouldBeTrue();
            shutdown.IsCompleted.ShouldBeFalse(
                "Shutdown must wait for the subject watcher's complete lifetime cleanup."
            );
            runtime.WatcherOperationCount.ShouldBe(1);
        }
        finally
        {
            cleanupRelease.TrySetResult();
        }

        if (cleanupFails)
        {
            await Should.ThrowAsync<AggregateException>(async () =>
                await shutdown.WaitAsync(TimeSpan.FromSeconds(5))
            );
        }
        else
        {
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        }
        // The cancellation source is disposed before tracking removal, so an empty dictionary
        // proves both lifetime resources were released.
        runtime.WatcherOperationCount.ShouldBe(0);
    }

    [Test]
    public async Task ShutdownDrainsSubjectWatcherCleanupAfterManualDisposal()
    {
        var store = new InMemoryStateSource<RuntimeLifetimeSettings.Fragment>(
            new RuntimeLifetimeSettings.Fragment { Label = "initial" }
        );
        var watcher = new ShutdownWatcher(delayCleanup: false);
        var runtime = new ConfiglueRuntime<
            RuntimeLifetimeSettings,
            RuntimeLifetimeSettings.Fragment
        >(
            new StateSourceSet<RuntimeLifetimeSettings.Fragment>([
                new("watcher", store, watcher: watcher),
            ]),
            onChangeDebounce: TimeSpan.Zero
        );
        var cleanupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var cleanupRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        runtime.WatcherCleanupBarrier = async () =>
        {
            cleanupEntered.TrySetResult();
            await cleanupRelease.Task;
        };

        ISubjectState<RuntimeLifetimeSettings> subjectState = runtime;
        var subscription = subjectState.ForSubject(new LifetimeSubject()).OnChange(_ => { });
        await watcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        subscription.Dispose();
        var shutdown = runtime.DisposeAsync().AsTask();
        try
        {
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            watcher.Canceled.Task.IsCompleted.ShouldBeTrue();
            runtime.WatcherOperationCount.ShouldBe(1);
            shutdown.IsCompleted.ShouldBeFalse(
                "Shutdown must drain cleanup started by an earlier manual subscription disposal."
            );
        }
        finally
        {
            cleanupRelease.TrySetResult();
        }

        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.WatcherOperationCount.ShouldBe(0);
    }

    private sealed record NamedScopedTestSubject(string TenantId, string UserId) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.FromSegments(TenantId, UserId);
    }

    private sealed class NamedScopedTestSubjectAccessor
        : IConfiglueSubjectAccessor<NamedScopedTestSubject>
    {
        private NamedScopedTestSubject? _subject;

        public async ValueTask<NamedScopedTestSubject> GetCurrentAsync(
            CancellationToken cancellationToken = default
        )
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return _subject
                ?? throw new InvalidOperationException("A test subject has not been selected.");
        }

        public async ValueTask<IConfiglueSubject> GetCurrentSubjectAsync(
            CancellationToken cancellationToken = default
        ) => await GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        public void Set(NamedScopedTestSubject subject) => _subject = subject;
    }

    private sealed class NamedScopedTrackingResource : IDisposable
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class NamedScopedTrackingSourceDefinition(
        string sourceId,
        InMemoryStateSource<AppSettings.Fragment> store,
        List<NamedScopedTrackingResource> created
    ) : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ArgumentNullException.ThrowIfNull(context);
            var resource = new NamedScopedTrackingResource();
            lock (created)
            {
                created.Add(resource);
            }

            context.Own(resource);
            var source = new StateSource<TFragment>(
                sourceId,
                (ISourceReader<TFragment>)(object)store,
                writer: (ISourceWriter<TFragment>)(object)store,
                watcher: (ISourceWatcher)(object)store
            );
            return context.Complete(source);
        }
    }

    private sealed record LifetimeSubject : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From("runtime-lifetime");
    }

    private sealed class ShutdownWatcher(bool delayCleanup) : ISourceWatcher
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        )
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                Canceled.TrySetResult();
                if (delayCleanup)
                {
                    await Release.Task;
                }
            }
        }
    }

    [Test]
    public void SharedSourceKeepsConfigurationRuntimeSharedAcrossScopes()
    {
        var store = new InMemoryStateSource<RuntimeLifetimeSettings.Fragment>();
        var services = new ServiceCollection();
        services.AddConfiglue(builder =>
            builder.Add<RuntimeLifetimeSettings>(model =>
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<RuntimeLifetimeSettings.Fragment>(
                            "store",
                            store,
                            writer: store
                        )
                    )
                )
            )
        );
        using var provider = services.BuildServiceProvider();
        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();

        var stateA = scopeA.ServiceProvider.GetRequiredService<
            IReadOnlyState<RuntimeLifetimeSettings>
        >();
        var stateB = scopeB.ServiceProvider.GetRequiredService<
            IReadOnlyState<RuntimeLifetimeSettings>
        >();

        ReferenceEquals(stateA, stateB).ShouldBeTrue();
        provider
            .GetRequiredService<ConfiglueContext>()
            .GetState<RuntimeLifetimeSettings>()
            .ShouldNotBeNull();
    }

    [Test]
    public void ScopedWebStorageSourceCreatesRuntimePerScope()
    {
        var services = new ServiceCollection();
        services.AddSingleton<FakeJsRuntime>();
        services.AddScoped<IJSRuntime>(provider => provider.GetRequiredService<FakeJsRuntime>());
        services.AddConfiglue(builder =>
            builder.Add<RuntimeLifetimeSettings>(model => model.UseLocalStorage("lifetime-ui"))
        );
        using var provider = services.BuildServiceProvider();
        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();

        var stateA = scopeA.ServiceProvider.GetRequiredService<
            IReadOnlyState<RuntimeLifetimeSettings>
        >();
        var stateB = scopeB.ServiceProvider.GetRequiredService<
            IReadOnlyState<RuntimeLifetimeSettings>
        >();

        ReferenceEquals(stateA, stateB).ShouldBeFalse();
        Should.Throw<KeyNotFoundException>(() =>
            provider.GetRequiredService<ConfiglueContext>().GetState<RuntimeLifetimeSettings>()
        );
    }

    [Test]
    public async Task ScopedWebStorageRuntimePersistsThroughItsScopedJavaScriptRuntime()
    {
        var services = new ServiceCollection();
        services.AddSingleton<FakeJsRuntime>();
        services.AddScoped<IJSRuntime>(provider => provider.GetRequiredService<FakeJsRuntime>());
        services.AddConfiglue(builder =>
            builder.Add<RuntimeLifetimeSettings>(model => model.UseLocalStorage("lifetime-ui"))
        );
        using var provider = services.BuildServiceProvider();
        var jsRuntime = provider.GetRequiredService<FakeJsRuntime>();
        using var scope = provider.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<
            IWritableState<RuntimeLifetimeSettings>
        >();

        await state.SaveAsync(
            new RuntimeLifetimeSettings.Patch
            {
                Label = FragmentOperation<string?>.Set("from-browser"),
            }
        );
        (await state.GetValueAsync()).Label.ShouldBe("from-browser");
        jsRuntime.Values.ShouldContainKey("lifetime-ui");
    }

    [Test]
    public async Task ScopedWebStorageRuntimeIsIsolatedBetweenCircuits()
    {
        var services = new ServiceCollection();
        services.AddScoped<FakeJsRuntime>();
        services.AddScoped<IJSRuntime>(provider => provider.GetRequiredService<FakeJsRuntime>());
        services.AddConfiglue(builder =>
            builder.Add<RuntimeLifetimeSettings>(model => model.UseLocalStorage("circuit-ui"))
        );
        using var provider = services.BuildServiceProvider();
        using var circuitA = provider.CreateScope();
        using var circuitB = provider.CreateScope();

        var runtimeA = circuitA.ServiceProvider.GetRequiredService<FakeJsRuntime>();
        var runtimeB = circuitB.ServiceProvider.GetRequiredService<FakeJsRuntime>();
        ReferenceEquals(runtimeA, runtimeB).ShouldBeFalse();

        var stateA = circuitA.ServiceProvider.GetRequiredService<
            IWritableState<RuntimeLifetimeSettings>
        >();
        var stateB = circuitB.ServiceProvider.GetRequiredService<
            IWritableState<RuntimeLifetimeSettings>
        >();

        await stateA.SaveAsync(
            new RuntimeLifetimeSettings.Patch
            {
                Label = FragmentOperation<string?>.Set("circuit-a"),
            }
        );
        await stateB.SaveAsync(
            new RuntimeLifetimeSettings.Patch
            {
                Label = FragmentOperation<string?>.Set("circuit-b"),
            }
        );

        (await stateA.GetValueAsync()).Label.ShouldBe("circuit-a");
        (await stateB.GetValueAsync()).Label.ShouldBe("circuit-b");
        runtimeA.Values.ShouldContainKey("circuit-ui");
        runtimeB.Values.ShouldContainKey("circuit-ui");
        runtimeA.Values["circuit-ui"].ShouldNotBe(runtimeB.Values["circuit-ui"]);
    }

    [Test]
    public async Task NamedScopedStatesResolveDistinctRuntimesPerStateName()
    {
        var firstStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var secondStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(2) }
        );
        var services = new ServiceCollection();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.StateName = "first";
                model.UseScopedRuntime();
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "first-store",
                            firstStore,
                            writer: firstStore
                        )
                    )
                );
            });
            builder.Add<AppSettings>(model =>
            {
                model.StateName = "second";
                model.UseScopedRuntime();
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "second-store",
                            secondStore,
                            writer: secondStore
                        )
                    )
                );
            });
        });
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }
        );
        using var scope = provider.CreateScope();

        var first = scope.ServiceProvider.GetRequiredKeyedService<IWritableState<AppSettings>>(
            "first"
        );
        var second = scope.ServiceProvider.GetRequiredKeyedService<IWritableState<AppSettings>>(
            "second"
        );

        (await first.GetValueAsync()).RetryCount.ShouldBe(1);
        (await second.GetValueAsync()).RetryCount.ShouldBe(2);
        ReferenceEquals(first, second).ShouldBeFalse();

        ReferenceEquals(
                first,
                scope.ServiceProvider.GetRequiredKeyedService<IWritableState<AppSettings>>("first")
            )
            .ShouldBeTrue();
        ReferenceEquals(
                first,
                scope.ServiceProvider.GetRequiredKeyedService<IReadOnlyState<AppSettings>>("first")
            )
            .ShouldBeTrue();

        ReferenceEquals(
                first,
                scope.ServiceProvider.GetRequiredKeyedService<ISubjectState<AppSettings>>("first")
            )
            .ShouldBeTrue();
        ReferenceEquals(
                first,
                scope.ServiceProvider.GetRequiredKeyedService<IConfiglueInspection<AppSettings>>(
                    "first"
                )
            )
            .ShouldBeTrue();
        ReferenceEquals(
                first,
                scope.ServiceProvider.GetRequiredKeyedService<IConfiglueEditSessions<AppSettings>>(
                    "first"
                )
            )
            .ShouldBeTrue();
        ReferenceEquals(
                first,
                scope.ServiceProvider.GetRequiredKeyedService<IConfiglueDiagnostics<AppSettings>>(
                    "first"
                )
            )
            .ShouldBeTrue();
        ReferenceEquals(
                first,
                scope.ServiceProvider.GetRequiredKeyedService<IConfiglueSources<AppSettings>>(
                    "first"
                )
            )
            .ShouldBeTrue();

        var firstDiagnostics = scope.ServiceProvider.GetRequiredKeyedService<
            IConfiglueDiagnostics<AppSettings>
        >("first");
        var secondDiagnostics = scope.ServiceProvider.GetRequiredKeyedService<
            IConfiglueDiagnostics<AppSettings>
        >("second");
        firstDiagnostics.GetDiagnostics().StateName.ShouldBe("first");
        secondDiagnostics.GetDiagnostics().StateName.ShouldBe("second");
        firstDiagnostics
            .GetDiagnostics()
            .Sources.Select(static source => source.Id)
            .ShouldBe([SourceId.From("first-store")]);
        secondDiagnostics
            .GetDiagnostics()
            .Sources.Select(static source => source.Id)
            .ShouldBe([SourceId.From("second-store")]);
        firstDiagnostics
            .GetDiagnostics()
            .DefaultWriteSourceId.ShouldBe(SourceId.From("first-store"));
        secondDiagnostics
            .GetDiagnostics()
            .DefaultWriteSourceId.ShouldBe(SourceId.From("second-store"));

        await first.SaveAsync(patch => patch.RetryCount = 11);
        (await first.GetValueAsync()).RetryCount.ShouldBe(11);
        (await second.GetValueAsync()).RetryCount.ShouldBe(2);
        (await firstStore.ReadAsync()).Value!.RetryCount.Value.ShouldBe(11);
        (await secondStore.ReadAsync()).Value!.RetryCount.Value.ShouldBe(2);

        await second.SaveAsync(patch => patch.RetryCount = 22);
        (await first.GetValueAsync()).RetryCount.ShouldBe(11);
        (await second.GetValueAsync()).RetryCount.ShouldBe(22);
        (await firstStore.ReadAsync()).Value!.RetryCount.Value.ShouldBe(11);
        (await secondStore.ReadAsync()).Value!.RetryCount.Value.ShouldBe(22);

        using var otherScope = provider.CreateScope();
        var firstInOtherScope = otherScope.ServiceProvider.GetRequiredKeyedService<
            IWritableState<AppSettings>
        >("first");
        ReferenceEquals(first, firstInOtherScope).ShouldBeFalse();
        (await firstInOtherScope.GetValueAsync()).RetryCount.ShouldBe(11);
    }

    [Test]
    public async Task DefaultAndNamedScopedStatesAreIsolated()
    {
        var defaultStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var namedStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(2) }
        );
        var services = new ServiceCollection();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.UseScopedRuntime();
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "default-store",
                            defaultStore,
                            writer: defaultStore
                        )
                    )
                );
            });
            builder.Add<AppSettings>(model =>
            {
                model.StateName = "named";
                model.UseScopedRuntime();
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "named-store",
                            namedStore,
                            writer: namedStore
                        )
                    )
                );
            });
        });
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }
        );
        using var scope = provider.CreateScope();

        var defaultState = scope.ServiceProvider.GetRequiredService<IWritableState<AppSettings>>();
        var namedState = scope.ServiceProvider.GetRequiredKeyedService<IWritableState<AppSettings>>(
            "named"
        );

        (await defaultState.GetValueAsync()).RetryCount.ShouldBe(1);
        (await namedState.GetValueAsync()).RetryCount.ShouldBe(2);
        ReferenceEquals(defaultState, namedState).ShouldBeFalse();

        scope
            .ServiceProvider.GetRequiredService<IConfiglueDiagnostics<AppSettings>>()
            .GetDiagnostics()
            .StateName.ShouldBe(string.Empty);
        scope
            .ServiceProvider.GetRequiredKeyedService<IConfiglueDiagnostics<AppSettings>>("named")
            .GetDiagnostics()
            .StateName.ShouldBe("named");

        await defaultState.SaveAsync(patch => patch.RetryCount = 11);
        (await defaultState.GetValueAsync()).RetryCount.ShouldBe(11);
        (await namedState.GetValueAsync()).RetryCount.ShouldBe(2);
        (await namedStore.ReadAsync()).Value!.RetryCount.Value.ShouldBe(2);

        using var otherScope = provider.CreateScope();
        ReferenceEquals(
                defaultState,
                otherScope.ServiceProvider.GetRequiredService<IWritableState<AppSettings>>()
            )
            .ShouldBeFalse();
    }

    [Test]
    public async Task NamedPerSubjectScopedStatesResolveDistinctRuntimes()
    {
        var firstUsers = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var secondUsers = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(2) }
        );
        var services = new ServiceCollection();
        services.AddScoped<NamedScopedTestSubjectAccessor>();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.StateName = "first";
                model.PerSubject<NamedScopedTestSubjectAccessor>();
                model.UseScopedRuntime();
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "first-users",
                            firstUsers,
                            writer: firstUsers,
                            watcher: firstUsers
                        )
                    )
                );
            });
            builder.Add<AppSettings>(model =>
            {
                model.StateName = "second";
                model.PerSubject<NamedScopedTestSubjectAccessor>();
                model.UseScopedRuntime();
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "second-users",
                            secondUsers,
                            writer: secondUsers,
                            watcher: secondUsers
                        )
                    )
                );
            });
        });
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }
        );
        using var scope = provider.CreateScope();
        var subject = new NamedScopedTestSubject("tenant", "user");
        scope.ServiceProvider.GetRequiredService<NamedScopedTestSubjectAccessor>().Set(subject);

        var first = scope.ServiceProvider.GetRequiredKeyedService<IWritableState<AppSettings>>(
            "first"
        );
        var second = scope.ServiceProvider.GetRequiredKeyedService<IWritableState<AppSettings>>(
            "second"
        );

        (await first.GetValueAsync()).RetryCount.ShouldBe(1);
        (await second.GetValueAsync()).RetryCount.ShouldBe(2);
        ReferenceEquals(first, second).ShouldBeFalse();
        ReferenceEquals(
                first,
                scope.ServiceProvider.GetRequiredKeyedService<IReadOnlyState<AppSettings>>("first")
            )
            .ShouldBeTrue();

        scope
            .ServiceProvider.GetRequiredKeyedService<IConfiglueDiagnostics<AppSettings>>("first")
            .GetDiagnostics()
            .StateName.ShouldBe("first");
        scope
            .ServiceProvider.GetRequiredKeyedService<IConfiglueDiagnostics<AppSettings>>("second")
            .GetDiagnostics()
            .StateName.ShouldBe("second");

        var firstDetails = await first.GetDetailsAsync();
        firstDetails.RetryCount.Source?.Resolution?.LogicalSubjectKey.ShouldBe(subject.Key);

        await first.SaveAsync(patch => patch.RetryCount = 11);
        (await first.GetValueAsync()).RetryCount.ShouldBe(11);
        (await second.GetValueAsync()).RetryCount.ShouldBe(2);

        using var otherScope = provider.CreateScope();
        otherScope
            .ServiceProvider.GetRequiredService<NamedScopedTestSubjectAccessor>()
            .Set(subject);
        var firstInOtherScope = otherScope.ServiceProvider.GetRequiredKeyedService<
            IWritableState<AppSettings>
        >("first");
        ReferenceEquals(first, firstInOtherScope).ShouldBeFalse();
    }

    [Test]
    public async Task NamedScopedRuntimesDisposeOwnedResourcesPerStateName()
    {
        var firstStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var secondStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(2) }
        );
        var createdResources = new List<NamedScopedTrackingResource>();
        var services = new ServiceCollection();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.StateName = "first";
                model.UseScopedRuntime();
                model.Sources(sources =>
                    ((IConfiglueSourceRegistrationSink)sources).Add(
                        new NamedScopedTrackingSourceDefinition(
                            "first-store",
                            firstStore,
                            createdResources
                        )
                    )
                );
            });
            builder.Add<AppSettings>(model =>
            {
                model.StateName = "second";
                model.UseScopedRuntime();
                model.Sources(sources =>
                    ((IConfiglueSourceRegistrationSink)sources).Add(
                        new NamedScopedTrackingSourceDefinition(
                            "second-store",
                            secondStore,
                            createdResources
                        )
                    )
                );
            });
        });
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }
        );
        var scope = provider.CreateScope();
        (
            await scope
                .ServiceProvider.GetRequiredKeyedService<IWritableState<AppSettings>>("first")
                .GetValueAsync()
        ).RetryCount.ShouldBe(1);
        (
            await scope
                .ServiceProvider.GetRequiredKeyedService<IWritableState<AppSettings>>("second")
                .GetValueAsync()
        ).RetryCount.ShouldBe(2);

        createdResources.Count.ShouldBe(2);
        scope.Dispose();

        createdResources.Count.ShouldBe(2);
        foreach (var resource in createdResources)
        {
            resource.DisposeCount.ShouldBe(1);
        }
    }

    [Test]
    public void ProcessWideContextRejectsScopedOnlySource()
    {
        var builder = new ConfiglueBuilder();
        builder.Add<RuntimeLifetimeSettings>(model => model.UseLocalStorage("lifetime-ui"));

        var exception = Should.Throw<InvalidOperationException>(() => builder.CreateContext());
        exception.Message.ShouldContain("scoped");
    }

    [Test]
    public async Task WebStorageResourceReportsUnavailableAndConflicts()
    {
        var fake = new FakeJsRuntime();
        var resource = new WebStorageResource(fake, WebStorageKind.Local, "direct");

        (await resource.ReadAsync()).Status.ShouldBe(StateReadStatus.NotFound);

        var first = await resource.WriteAsync(new ResourceWriteRequest("one"u8.ToArray()));
        first.Revision.ShouldNotBeNullOrEmpty();
        var read = await resource.ReadAsync();
        read.Status.ShouldBe(StateReadStatus.Success);
        read.Revision.ShouldBe(first.Revision);

        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest("two"u8.ToArray(), RevisionCondition.Match("stale"))
            )
        );

        fake.Available = false;
        (await resource.ReadAsync()).Status.ShouldBe(StateReadStatus.Unavailable);
        await Should.ThrowAsync<WebStorageUnavailableException>(async () =>
            await resource.WriteAsync(new ResourceWriteRequest("three"u8.ToArray()))
        );
    }

    private sealed class FakeJsRuntime : IJSRuntime
    {
        public bool Available { get; set; } = true;

        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, default, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args
        )
        {
            EnsureAvailable();
            if (string.Equals(identifier, "import", StringComparison.Ordinal))
            {
                return ValueTask.FromResult((TValue)(object)new FakeWebStorageModule(this));
            }

            var key = (string)args![0]!;
            if (identifier.EndsWith(".getItem", StringComparison.Ordinal))
            {
                Values.TryGetValue(key, out var value);
                return ValueTask.FromResult((TValue)(object?)value!);
            }

            if (identifier.EndsWith(".setItem", StringComparison.Ordinal))
            {
                Values[key] = (string)args[1]!;
                return ValueTask.FromResult(default(TValue)!);
            }

            throw new NotSupportedException(identifier);
        }

        public ValueTask InvokeVoidAsync(string identifier, object?[]? args) =>
            InvokeVoidAsync(identifier, default, args);

        public ValueTask InvokeVoidAsync(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args
        )
        {
            EnsureAvailable();
            if (!identifier.EndsWith(".setItem", StringComparison.Ordinal))
            {
                throw new NotSupportedException(identifier);
            }

            var key = (string)args![0]!;
            Values[key] = (string)args[1]!;
            return ValueTask.CompletedTask;
        }

        private string Mutate(object?[] args)
        {
            var key = (string)args[1]!;
            var value = (string)args[2]!;
            var expectedRevision = args[3] as string;
            var mustNotExist = (bool)args[4]!;

            Values.TryGetValue(key, out var existing);
            var exists = existing is not null;
            if (mustNotExist)
            {
                if (exists)
                {
                    return "conflict";
                }
            }
            else
            {
                var revision = exists ? RevisionOf(existing!) : null;
                if (!string.Equals(revision, expectedRevision, StringComparison.Ordinal))
                {
                    return "conflict";
                }
            }

            Values[key] = value;
            return "committed";
        }

        private static string? RevisionOf(string raw)
        {
            try
            {
                using var document = JsonDocument.Parse(raw);
                if (
                    document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String
                )
                {
                    return
                        document.RootElement.TryGetProperty("revision", out var revision)
                        && revision.ValueKind == JsonValueKind.String
                        ? revision.GetString()
                        : null;
                }
            }
            catch (JsonException)
            {
                // Not a Configlue envelope.
            }

            return null;
        }

        private void EnsureAvailable()
        {
            if (!Available)
            {
                throw new InvalidOperationException("JavaScript is unavailable.");
            }
        }

        private sealed class FakeWebStorageModule(FakeJsRuntime owner) : IJSObjectReference
        {
            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
                InvokeAsync<TValue>(identifier, default, args);

            public ValueTask<TValue> InvokeAsync<TValue>(
                string identifier,
                CancellationToken cancellationToken,
                object?[]? args
            )
            {
                owner.EnsureAvailable();
                if (string.Equals(identifier, "mutate", StringComparison.Ordinal))
                {
                    return ValueTask.FromResult((TValue)(object)owner.Mutate(args!));
                }

                if (
                    string.Equals(identifier, "subscribeStorageChanges", StringComparison.Ordinal)
                    || string.Equals(
                        identifier,
                        "unsubscribeStorageChanges",
                        StringComparison.Ordinal
                    )
                )
                {
                    // No external browsing contexts exist in these tests; subscriptions are
                    // accepted and released without delivering events.
                    return ValueTask.FromResult(default(TValue)!);
                }

                throw new NotSupportedException(identifier);
            }

            ValueTask IAsyncDisposable.DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
