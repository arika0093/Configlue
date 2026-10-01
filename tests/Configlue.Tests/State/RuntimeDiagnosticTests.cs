using Configlue.Testing;

namespace Configlue.Tests;

public sealed class RuntimeDiagnosticTests
{
    [Test]
    public async Task ConcurrentResolutions_RetainOnlyABoundedOrderedHistory()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = 3 }
        );
        await using var runtime = CreateRuntime([new("store", store)], capacity: 17);
        await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ => Task.Run(async () => await runtime.GetValueAsync()))
        );
        var history = runtime.GetRecentEvents();
        history.Count.ShouldBe(17);
        history
            .Select(static item => item.Sequence)
            .ShouldBe(Enumerable.Range(112, 17).Select(static sequence => (long)sequence));
        runtime.GetRuntimeSnapshot().Sources.Count.ShouldBe(1);
    }

    [Test]
    public async Task ContextAndDynamicStates_PreserveDiagnosticOptions()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = 3 }
        );
        await using var context = ConfiglueApp.CreateContext(builder =>
            builder.Add<AppSettings>(model =>
            {
                model.EnableDynamicStates = true;
                model.Diagnostics = new ConfiglueRuntimeDiagnosticOptions
                {
                    EventHistoryCapacity = 8,
                };
                model.ConfigureSources(registration =>
                    registration.Sources.Add(new StateSource<AppSettings.Fragment>("store", store))
                );
            })
        );
        await context.GetState<AppSettings>().GetValueAsync();
        context.GetDiagnostics<AppSettings>().GetRecentEvents().Count.ShouldBe(4);
        context.GetStateRegistry<AppSettings>().TryAdd("alternate").ShouldBeTrue();
        await context.GetState<AppSettings>("alternate").GetValueAsync();
        var alternate = context.GetDiagnostics<AppSettings>("alternate");
        alternate.GetRuntimeSnapshot().StateName.ShouldBe("alternate");
        alternate.GetRecentEvents().Count.ShouldBe(4);
        context.GetDiagnostics<AppSettings>().GetRecentEvents().Count.ShouldBe(4);
    }

    [Test]
    public async Task Resolution_RecordsOrderedEvents_Fallback_AndParentOperations()
    {
        var empty = new InMemoryStateSource<AppSettings.Fragment>();
        var loaded = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = "secret-value" }
        );
        await using var runtime = CreateRuntime(
            [
                new("missing", empty, priority: 100),
                new(
                    "loaded",
                    loaded,
                    physicalOrigin: "secret-path",
                    resourceId: new ResourceId("secret-resource")
                ),
            ],
            capacity: 32
        );
        IConfiglueDiagnostics<AppSettings> diagnostics = runtime;
        diagnostics.GetRuntimeSnapshot().LastResolution.ShouldBeNull();

        (await runtime.GetValueAsync()).Label.ShouldBe("secret-value");

        var events = diagnostics.GetRecentEvents();
        events
            .Select(static item => item.Kind)
            .ShouldBe(
                new[]
                {
                    ConfiglueDiagnosticEventKind.ResolveStarted,
                    ConfiglueDiagnosticEventKind.SourceReadStarted,
                    ConfiglueDiagnosticEventKind.SourceReadCompleted,
                    ConfiglueDiagnosticEventKind.SourceFallback,
                    ConfiglueDiagnosticEventKind.SourceReadStarted,
                    ConfiglueDiagnosticEventKind.SourceReadCompleted,
                    ConfiglueDiagnosticEventKind.ResolveCompleted,
                }
            );
        events
            .Select(static item => item.Sequence)
            .ShouldBe(Enumerable.Range(1, 7).Select(static number => (long)number));
        events[1].ParentOperationId.ShouldBe(events[0].OperationId);
        events[2].OperationId.ShouldBe(events[1].OperationId);
        events[^1].OperationId.ShouldBe(events[0].OperationId);
        events[2].ReadStatus.ShouldBe(StateReadStatus.NotFound);
        var snapshot = diagnostics.GetRuntimeSnapshot();
        snapshot.LastResolution!.Value.ReadStatus.ShouldBe(StateReadStatus.Success);
        snapshot
            .Sources.Single(static source => source.Id == "loaded")
            .LastSuccessfulRead.ShouldNotBeNull();
        string.Join("\n", events).ShouldNotContain("secret-");
        System.Text.Json.JsonSerializer.Serialize(snapshot).ShouldNotContain("secret-");
    }

    [Test]
    public async Task Snapshot_IsIoFree_AndAvailableWhileAReadIsBlocked()
    {
        var reader = new ProbeReader();
        await using var runtime = CreateRuntime([new("remote", reader)]);
        runtime.GetRuntimeSnapshot().LastResolution.ShouldBeNull();
        runtime.GetRecentEvents().ShouldBeEmpty();
        reader.ReadCount.ShouldBe(0);
        var read = runtime.GetValueAsync().AsTask();
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var snapshot = await Task.Run(runtime.GetRuntimeSnapshot)
                .WaitAsync(TimeSpan.FromSeconds(2));
            snapshot.LastResolution.ShouldBeNull();
            reader.ReadCount.ShouldBe(1);
        }
        finally
        {
            reader.Release.TrySetResult(true);
            await read.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var observed = runtime.GetRuntimeSnapshot();
        observed.LastResolution.ShouldNotBeNull();
        reader.ReadCount.ShouldBe(1);
        (await runtime.GetValueAsync()).ShouldNotBeNull();
        observed.LastResolution!.Value.Sequence.ShouldBeLessThan(
            runtime.GetRuntimeSnapshot().LastResolution!.Value.Sequence
        );
        observed
            .Sources.Single()
            .LastRead!.Value.Sequence.ShouldBeLessThan(
                runtime.GetRuntimeSnapshot().Sources.Single().LastRead!.Value.Sequence
            );
    }

    [Test]
    public async Task History_IsBounded_Immutable_AndCanBeDisabled()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = 3 }
        );
        await using var runtime = CreateRuntime([new("store", store)], capacity: 3);
        await runtime.GetValueAsync();
        var first = runtime.GetRecentEvents();
        first.Count.ShouldBe(3);
        for (var index = 0; index < 20; index++)
            await runtime.GetValueAsync();
        var latest = runtime.GetRecentEvents();
        latest.Count.ShouldBe(3);
        first[^1].Sequence.ShouldBe(4);
        latest[^1].Sequence.ShouldBe(84);
        latest.Select(static item => item.Sequence).ShouldBe(new long[] { 82, 83, 84 });

        await using var noHistory = CreateRuntime([new("store", store)]);
        await noHistory.GetValueAsync();
        noHistory.GetRecentEvents().ShouldBeEmpty();
        noHistory.GetRuntimeSnapshot().LastResolution.ShouldNotBeNull();
    }

    [Test]
    public async Task Listeners_WorkWithTrackingDisabled_AndCannotBreakOperations()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = 3 }
        );
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new("store", store)]),
            diagnostics: ConfiglueRuntimeDiagnosticOptions.Disabled
        );
        var received = new List<ConfiglueDiagnosticEvent>();
        using var failing = runtime.OnDiagnosticEvent(_ =>
            throw new InvalidOperationException("listener-secret")
        );
        var subscription = runtime.OnDiagnosticEvent(received.Add);
        (await runtime.GetValueAsync()).RetryCount.ShouldBe(3);
        received.Count.ShouldBe(4);
        runtime.GetRuntimeSnapshot().LastResolution.ShouldBeNull();
        runtime.GetRecentEvents().ShouldBeEmpty();
        subscription.Dispose();
        subscription.Dispose();
        await runtime.GetValueAsync();
        received.Count.ShouldBe(4);
    }

    [Test]
    public async Task Failures_PreserveOriginalExceptions_AndDoNotRetainMessages()
    {
        var exception = new InvalidOperationException("credential=secret-password");
        await using var runtime = CreateRuntime(
            [new("bad", new ThrowingReader(exception))],
            capacity: 16
        );
        var thrown = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await runtime.GetValueAsync()
        );
        ReferenceEquals(thrown, exception).ShouldBeTrue();
        var events = runtime.GetRecentEvents();
        events
            .Select(static item => item.Kind)
            .ShouldBe(
                new[]
                {
                    ConfiglueDiagnosticEventKind.ResolveStarted,
                    ConfiglueDiagnosticEventKind.SourceReadStarted,
                    ConfiglueDiagnosticEventKind.SourceReadFailed,
                    ConfiglueDiagnosticEventKind.ResolveFailed,
                }
            );
        events[^1].ErrorCategory.ShouldBe(typeof(InvalidOperationException).FullName);
        string.Join("\n", events).ShouldNotContain("secret-password");
        runtime
            .GetRuntimeSnapshot()
            .Sources.Single()
            .LastRead!.Value.ErrorCategory.ShouldBe(typeof(InvalidOperationException).FullName);
    }

    [Test]
    public async Task CallerCancellation_IsDistinguishedFromReadFailures()
    {
        var reader = new ProbeReader();
        await using var runtime = CreateRuntime([new("remote", reader)], capacity: 16);
        using var cancellation = new CancellationTokenSource();
        var read = runtime.GetValueAsync(cancellation.Token).AsTask();
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await read);
        runtime.GetRecentEvents()[^1].Canceled.ShouldBeTrue();
        runtime.GetRuntimeSnapshot().Sources.Single().LastRead!.Value.Canceled.ShouldBeTrue();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(4097)]
    public void InvalidHistoryCapacity_IsRejected(int capacity) =>
        Should.Throw<InvalidOperationException>(() =>
            CreateRuntime([new("store", new InMemoryStateSource<AppSettings.Fragment>())], capacity)
        );

    [Test]
    public async Task InvalidEffectiveValues_EmitValueFreeValidationEvents()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = 101, Label = "validation-secret" }
        );
        await using var runtime = CreateRuntime([new("store", store)], capacity: 16);
        await Should.ThrowAsync<ConfiglueValidationException>(async () =>
            await runtime.GetValueAsync()
        );
        runtime
            .GetRecentEvents()
            .Select(static item => item.Kind)
            .ShouldContain(ConfiglueDiagnosticEventKind.ValidationFailed);
        string.Join("\n", runtime.GetRecentEvents()).ShouldNotContain("validation-secret");
    }

    private static ConfiglueRuntime<AppSettings, AppSettings.Fragment> CreateRuntime(
        StateSource<AppSettings.Fragment>[] sources,
        int capacity = 0
    ) =>
        new(
            new StateSourceSet<AppSettings.Fragment>(sources),
            diagnostics: new ConfiglueRuntimeDiagnosticOptions { EventHistoryCapacity = capacity }
        );

    private sealed class ProbeReader : ISourceReader<AppSettings.Fragment>
    {
        internal TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ReadCount { get; private set; }

        public async ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            ReadCount++;
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return StateReadResult<AppSettings.Fragment>.Success(
                new AppSettings.Fragment { RetryCount = 3 }
            );
        }
    }

    private sealed class ThrowingReader(Exception exception) : ISourceReader<AppSettings.Fragment>
    {
        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => throw exception;
    }
}
