using Configlue.Testing;

namespace Configlue.Tests;

public sealed partial class RuntimeDiagnosticTests
{
    [Test]
    public void DiagnosticPayloadsExposeObservationWithoutPositionalConstruction()
    {
        var eventType = typeof(ConfiglueDiagnosticEvent);
        eventType
            .GetConstructors()
            .Where(static constructor => constructor.GetParameters().Length > 0)
            .ShouldBeEmpty();
        eventType.GetMethod("Deconstruct").ShouldBeNull();
        foreach (var property in eventType.GetProperties())
        {
            property.SetMethod.ShouldBeNull();
        }

        var snapshotType = typeof(ConfiglueRuntimeSourceSnapshot);
        snapshotType
            .GetConstructors()
            .Where(static constructor => constructor.GetParameters().Length > 0)
            .ShouldBeEmpty();
        snapshotType.GetMethod("Deconstruct").ShouldBeNull();
        foreach (var property in snapshotType.GetProperties())
        {
            property.SetMethod.ShouldBeNull();
        }

        var runtimeSnapshotType = typeof(ConfiglueRuntimeDiagnosticSnapshot);
        runtimeSnapshotType
            .GetConstructors()
            .Where(static constructor => constructor.GetParameters().Length > 0)
            .ShouldBeEmpty();
        runtimeSnapshotType.GetMethod("Deconstruct").ShouldBeNull();
        foreach (var property in runtimeSnapshotType.GetProperties())
        {
            property.SetMethod.ShouldBeNull();
        }
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
                model.ConfigureSources(registration =>
                    registration.Sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "store",
                            store,
                            new StateSourceOptions<AppSettings.Fragment>()
                        )
                    )
                );
            })
        );
        await context.GetState<AppSettings>().GetValueAsync();
        context.GetDiagnostics<AppSettings>().GetRuntimeSnapshot().LastResolution.ShouldNotBeNull();
        (await context.GetStateRegistry<AppSettings>().TryAddAsync("alternate")).ShouldBeTrue();
        await context.GetState<AppSettings>("alternate").GetValueAsync();
        var alternate = context.GetDiagnostics<AppSettings>("alternate");
        alternate.GetRuntimeSnapshot().StateName.ShouldBe("alternate");
        alternate.GetRuntimeSnapshot().LastResolution.ShouldNotBeNull();
        context.GetDiagnostics<AppSettings>().GetRuntimeSnapshot().StateName.ShouldBe(string.Empty);
    }

    [Test]
    public async Task Resolution_RecordsOutcomeSnapshot_WithFallback()
    {
        var empty = new InMemoryStateSource<AppSettings.Fragment>();
        var loaded = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = "secret-value" }
        );
        await using var runtime = CreateRuntime([
            new StateSource<AppSettings.Fragment>(
                "missing",
                empty,
                new StateSourceOptions<AppSettings.Fragment> { Priority = 100 }
            ),
            new StateSource<AppSettings.Fragment>(
                "loaded",
                loaded,
                new StateSourceOptions<AppSettings.Fragment>
                {
                    PhysicalOrigin = "secret-path",
                    FixedResourceId = new ResourceId("secret-resource"),
                }
            ),
        ]);
        IConfiglueDiagnostics<AppSettings> diagnostics = runtime;
        diagnostics.GetRuntimeSnapshot().LastResolution.ShouldBeNull();

        (await runtime.GetValueAsync()).Label.ShouldBe("secret-value");

        var snapshot = diagnostics.GetRuntimeSnapshot();
        var resolution = snapshot.LastResolution!.Value;
        resolution.Kind.ShouldBe(ConfiglueDiagnosticEventKind.ResolveCompleted);
        resolution.ReadStatus.ShouldBe(StateReadStatus.Success);
        resolution.Duration.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
        snapshot
            .Sources.Single(static source => source.Id == SourceId.From("loaded"))
            .LastSuccessfulRead.ShouldNotBeNull();
        snapshot
            .Sources.Single(static source => source.Id == SourceId.From("missing"))
            .LastRead!.Value.ReadStatus.ShouldBe(StateReadStatus.NotFound);
        string.Join(
                "\n",
                snapshot
                    .Sources.Select(static source => source.LastRead)
                    .Append(snapshot.LastResolution)
            )
            .ShouldNotContain("secret-");
        System.Text.Json.JsonSerializer.Serialize(snapshot).ShouldNotContain("secret-");
    }

    [Test]
    public async Task Snapshot_IsIoFree_AndAvailableWhileAReadIsBlocked()
    {
        var reader = new ProbeReader();
        await using var runtime = CreateRuntime([
            new StateSource<AppSettings.Fragment>(
                "remote",
                reader,
                new StateSourceOptions<AppSettings.Fragment>()
            ),
        ]);
        runtime.GetRuntimeSnapshot().LastResolution.ShouldBeNull();
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
        runtime
            .GetRuntimeSnapshot()
            .LastResolution!.Value.Timestamp.ShouldBeGreaterThanOrEqualTo(
                observed.LastResolution!.Value.Timestamp
            );
        runtime
            .GetRuntimeSnapshot()
            .Sources.Single()
            .LastRead!.Value.Timestamp.ShouldBeGreaterThanOrEqualTo(
                observed.Sources.Single().LastRead!.Value.Timestamp
            );
    }

    [Test]
    public async Task Snapshot_CanBeDisabled()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = 3 }
        );
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "store",
                    store,
                    new StateSourceOptions<AppSettings.Fragment>()
                ),
            ]),
            diagnostics: ConfiglueRuntimeDiagnosticOptions.Disabled
        );
        (await runtime.GetValueAsync()).RetryCount.ShouldBe(3);
        runtime.GetRuntimeSnapshot().LastResolution.ShouldBeNull();
        runtime.GetRuntimeSnapshot().Sources.Single().LastRead.ShouldBeNull();
    }

    [Test]
    public async Task Failures_PreserveOriginalExceptions_AndDoNotRetainMessages()
    {
        var exception = new InvalidOperationException("credential=secret-password");
        await using var runtime = CreateRuntime([
            new StateSource<AppSettings.Fragment>(
                "bad",
                new ThrowingReader(exception),
                new StateSourceOptions<AppSettings.Fragment>()
            ),
        ]);
        var thrown = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await runtime.GetValueAsync()
        );
        ReferenceEquals(thrown, exception).ShouldBeTrue();
        var snapshot = runtime.GetRuntimeSnapshot();
        var last = snapshot.LastResolution!.Value;
        last.Kind.ShouldBe(ConfiglueDiagnosticEventKind.ResolveFailed);
        last.ErrorCategory.ShouldBe(typeof(InvalidOperationException).FullName);
        snapshot
            .Sources.Single()
            .LastRead!.Value.Kind.ShouldBe(ConfiglueDiagnosticEventKind.SourceReadFailed);
        snapshot
            .Sources.Single()
            .LastRead!.Value.ErrorCategory.ShouldBe(typeof(InvalidOperationException).FullName);
        string.Join("\n", new[] { last, snapshot.Sources.Single().LastRead!.Value })
            .ShouldNotContain("secret-password");
    }

    [Test]
    public async Task CallerCancellation_IsDistinguishedFromReadFailures()
    {
        var reader = new ProbeReader();
        await using var runtime = CreateRuntime([
            new StateSource<AppSettings.Fragment>(
                "remote",
                reader,
                new StateSourceOptions<AppSettings.Fragment>()
            ),
        ]);
        using var cancellation = new CancellationTokenSource();
        var read = runtime.GetValueAsync(cancellation.Token).AsTask();
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await read);
        var last = runtime.GetRuntimeSnapshot().Sources.Single().LastRead!.Value;
        last.Canceled.ShouldBeTrue();
        runtime.GetRuntimeSnapshot().LastResolution!.Value.Canceled.ShouldBeTrue();
    }

    [Test]
    public async Task InvalidEffectiveValues_EmitValueFreeValidationEvents()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = 101, Label = "validation-secret" }
        );
        await using var runtime = CreateRuntime([
            new StateSource<AppSettings.Fragment>(
                "store",
                store,
                new StateSourceOptions<AppSettings.Fragment>()
            ),
        ]);
        await Should.ThrowAsync<ConfiglueValidationException>(async () =>
            await runtime.GetValueAsync()
        );
        var last = runtime.GetRuntimeSnapshot().LastResolution!.Value;
        last.Kind.ShouldBe(ConfiglueDiagnosticEventKind.ResolveFailed);
        last.ErrorCategory.ShouldBe(typeof(ConfiglueValidationException).FullName);
        string.Join("\n", new[] { last }).ShouldNotContain("validation-secret");
        System
            .Text.Json.JsonSerializer.Serialize(runtime.GetRuntimeSnapshot())
            .ShouldNotContain("validation-secret");
    }

    private static ConfiglueRuntime<AppSettings, AppSettings.Fragment> CreateRuntime(
        StateSource<AppSettings.Fragment>[] sources
    ) => new(new StateSourceSet<AppSettings.Fragment>(sources));

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
