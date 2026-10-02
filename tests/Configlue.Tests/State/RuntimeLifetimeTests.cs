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
            var completed = await Task.WhenAny(shutdown, Task.Delay(100));
            ReferenceEquals(completed, shutdown)
                .ShouldBeFalse(
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
        var subscription = subjectState
            .ForSubject(new LifetimeSubject())
            .OnChange(_ => { });
        await delayed.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        subscription.Dispose();
        await delayed.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdown = runtime.DisposeAsync().AsTask();
        try
        {
            var completed = await Task.WhenAny(shutdown, Task.Delay(100));
            ReferenceEquals(completed, shutdown)
                .ShouldBeFalse(
                    "Shutdown must drain a subject watcher whose subscription was already disposed."
                );
        }
        finally
        {
            delayed.Release.TrySetResult();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
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
            if (string.Equals(identifier, "configlueWebStorage.acquire", StringComparison.Ordinal))
            {
                return ValueTask.FromResult((TValue)(object)"web-locks");
            }

            if (string.Equals(identifier, "configlueWebStorage.release", StringComparison.Ordinal))
            {
                return ValueTask.FromResult(default(TValue)!);
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

        private void EnsureAvailable()
        {
            if (!Available)
            {
                throw new InvalidOperationException("JavaScript is unavailable.");
            }
        }
    }
}
