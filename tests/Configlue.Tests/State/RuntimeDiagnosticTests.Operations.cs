using Configlue.Provider.Json;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed partial class RuntimeDiagnosticTests
{
    [Test]
    public async Task Writes_RecordThePhysicalOperationAndRevisionPresence()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = 3 }
        );
        await using var runtime = CreateRuntime([new StateSource<AppSettings.Fragment>("store", store, new StateSourceOptions<AppSettings.Fragment> { Writer = store })], capacity: 64);
        await runtime.SaveAsync(
            new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }
        );
        var writes = runtime
            .GetRecentEvents()
            .Where(static item =>
                item.Kind
                    is ConfiglueDiagnosticEventKind.WriteStarted
                        or ConfiglueDiagnosticEventKind.WriteCompleted
            )
            .ToArray();
        writes.Length.ShouldBe(2);
        writes[1].OperationId.ShouldBe(writes[0].OperationId);
        writes[1].SourceId.ShouldBe(SourceId.From("store"));
        writes[1].HasRevision.ShouldBeTrue();
        runtime.GetRuntimeSnapshot().LastWrite!.Value.ShouldBe(writes[1]);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task WriteFailures_DistinguishConflicts_AndPreserveExceptions(bool conflict)
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = 3 }
        );
        Exception exception = conflict
            ? new StateConflictException("secret-conflict")
            : new IOException("secret-failure");
        await using var runtime = CreateRuntime(
            [new StateSource<AppSettings.Fragment>("store", store, new StateSourceOptions<AppSettings.Fragment> { Writer = new DiagnosticFailingWriter(exception) })],
            capacity: 64
        );
        Exception? observed = null;
        try
        {
            await runtime.SaveAsync(
                new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }
            );
        }
        catch (Exception failure)
        {
            observed = failure;
        }
        ReferenceEquals(observed, exception).ShouldBeTrue();
        var last = runtime.GetRuntimeSnapshot().LastWrite!.Value;
        last.Kind.ShouldBe(
            conflict
                ? ConfiglueDiagnosticEventKind.WriteConflict
                : ConfiglueDiagnosticEventKind.WriteFailed
        );
        last.SourceId.ShouldBe(SourceId.From("store"));
        last.ErrorCategory.ShouldBe(exception.GetType().FullName);
        last.OperationId.ShouldBeGreaterThan(0);
        string.Join("\n", runtime.GetRecentEvents()).ShouldNotContain("secret-");
    }

    [Test]
    public async Task BatchedJsonSections_RecordOnePhysicalWrite()
    {
        var resource = new InMemoryResource();
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        IResourceReader firstSection = new JsonSectionResource(resource, "First");
        var first = new StateSource<AppSettings.Fragment>(
            "first",
            new SerializedSource<AppSettings.Fragment>(
                firstSection,
                codec,
                writer: firstSection as IResourceWriter,
                watcher: firstSection as ISourceWatcher
            ),
            new StateSourceOptions<AppSettings.Fragment>()
        );
        IResourceReader secondSection = new JsonSectionResource(resource, "Second");
        var second = new StateSource<AppSettings.Fragment>(
            "second",
            new SerializedSource<AppSettings.Fragment>(
                secondSection,
                codec,
                writer: secondSection as IResourceWriter,
                watcher: secondSection as ISourceWatcher
            ),
            new StateSourceOptions<AppSettings.Fragment>()
        );
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([first, second]),
            StateWritePlan.DefaultTo(SourceId.From("first")),
            diagnostics: new ConfiglueRuntimeDiagnosticOptions { EventHistoryCapacity = 64 }
        );
        await runtime.ApplyPatchesAsync([
            new StateSourcePatch(SourceId.From("first"), new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }),
            new StateSourcePatch(SourceId.From("second"), new AppSettings.Patch { Label = FragmentOperation<string?>.Set("section-secret") }),
        ]);
        resource.WriteCount.ShouldBe(1);
        runtime
            .GetRecentEvents()
            .Count(static item => item.Kind == ConfiglueDiagnosticEventKind.WriteStarted)
            .ShouldBe(1);
        runtime
            .GetRecentEvents()
            .Count(static item => item.Kind == ConfiglueDiagnosticEventKind.WriteCompleted)
            .ShouldBe(1);
        runtime.GetRuntimeSnapshot().LastWrite!.Value.HasRevision.ShouldBeTrue();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Reloads_ReportWhetherTheEffectiveValueChanged(bool changeValue)
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = 3 }
        );
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new StateSource<AppSettings.Fragment>("store", store, new StateSourceOptions<AppSettings.Fragment> { Watcher = store })]),
            onChangeDebounce: TimeSpan.Zero,
            diagnostics: new ConfiglueRuntimeDiagnosticOptions { EventHistoryCapacity = 64 }
        );
        var watching = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var reloaded = new TaskCompletionSource<ConfiglueDiagnosticEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var diagnostics = runtime.OnDiagnosticEvent(item =>
        {
            if (item.Kind == ConfiglueDiagnosticEventKind.WatchStarted)
                watching.TrySetResult(true);
            if (item.Kind == ConfiglueDiagnosticEventKind.ReloadCompleted)
                reloaded.TrySetResult(item);
        });
        using var subscription = runtime.OnChange(static _ => { });
        await watching.Task.WaitAsync(TimeSpan.FromSeconds(5));
        store.Set(new AppSettings.Fragment { RetryCount = changeValue ? 7 : 3 });
        var completed = await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        completed.EffectiveValueChanged.ShouldBe(changeValue);
        runtime.GetRuntimeSnapshot().LastReload!.Value.ShouldBe(completed);
        var events = runtime.GetRecentEvents();
        events
            .Count(static item => item.Kind == ConfiglueDiagnosticEventKind.EffectiveValueChanged)
            .ShouldBe(changeValue ? 1 : 0);
        var resolution = events.Last(static item =>
            item.Kind == ConfiglueDiagnosticEventKind.ResolveStarted
        );
        resolution.ParentOperationId.ShouldBe(completed.OperationId);
        runtime.GetRuntimeSnapshot().Sources.Single().LastWatchSignal.ShouldNotBeNull();
        await runtime.DisposeAsync();
        runtime.GetRuntimeSnapshot().Sources.Single().IsWatching.ShouldBeFalse();
    }

    [Test]
    public async Task FailedReloads_RecordTheObservedStatusWithoutRetainingValues()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = 3 }
        );
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new StateSource<AppSettings.Fragment>("store", store, new StateSourceOptions<AppSettings.Fragment> { Watcher = store })]),
            onChangeDebounce: TimeSpan.Zero,
            diagnostics: new ConfiglueRuntimeDiagnosticOptions { EventHistoryCapacity = 64 }
        );
        var watching = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var failed = new TaskCompletionSource<ConfiglueDiagnosticEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var diagnostics = runtime.OnDiagnosticEvent(item =>
        {
            if (item.Kind == ConfiglueDiagnosticEventKind.WatchStarted)
                watching.TrySetResult(true);
            if (item.Kind == ConfiglueDiagnosticEventKind.ReloadFailed)
                failed.TrySetResult(item);
        });
        using var subscription = runtime.OnChange(static _ => { });
        await watching.Task.WaitAsync(TimeSpan.FromSeconds(5));
        store.SetUnavailable();
        var last = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        last.ReadStatus.ShouldBe(StateReadStatus.Unavailable);
        runtime.GetRuntimeSnapshot().LastReload!.Value.ShouldBe(last);
    }

    [Test]
    public async Task ConcurrentSubjectWatches_KeepTheSourceWatchingUntilTheLastWaitStops()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = 3 }
        );
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new StateSource<AppSettings.Fragment>("store", store, new StateSourceOptions<AppSettings.Fragment> { Watcher = store })])
        );
        var started = 0;
        var bothStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var oneStopped = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var diagnostic = runtime.OnDiagnosticEvent(item =>
        {
            if (
                item.Kind == ConfiglueDiagnosticEventKind.WatchStarted
                && Interlocked.Increment(ref started) == 2
            )
                bothStarted.TrySetResult(true);
            if (item.Kind == ConfiglueDiagnosticEventKind.WatchStopped)
                oneStopped.TrySetResult(true);
        });
        using var first = runtime
            .ForSubject(new DiagnosticSubject("secret-first"))
            .OnChange(static _ => { });
        using var second = runtime
            .ForSubject(new DiagnosticSubject("secret-second"))
            .OnChange(static _ => { });
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        first.Dispose();
        await oneStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.GetRuntimeSnapshot().Sources.Single().IsWatching.ShouldBeTrue();
        await runtime.DisposeAsync();
        runtime.GetRuntimeSnapshot().Sources.Single().IsWatching.ShouldBeFalse();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task SourceMigrations_RecordSuccessAndFailure(bool succeeds)
    {
        var source = new InMemoryStateSource<AppSettings.Fragment>();
        if (succeeds)
            source.Set(new AppSettings.Fragment { RetryCount = 7 });
        var target = new InMemoryStateSource<AppSettings.Fragment>();
        await using var runtime = CreateRuntime(
            [new StateSource<AppSettings.Fragment>("source", source, new StateSourceOptions<AppSettings.Fragment>()), new StateSource<AppSettings.Fragment>("target", target, new StateSourceOptions<AppSettings.Fragment> { Writer = target })],
            capacity: 64
        );
        if (succeeds)
            await runtime.MigrateSourceAsync(SourceId.From("source"), SourceId.From("target"));
        else
            await Should.ThrowAsync<InvalidOperationException>(async () =>
                await runtime.MigrateSourceAsync(SourceId.From("source"), SourceId.From("target"))
            );
        var last = runtime.GetRuntimeSnapshot().LastMigration!.Value;
        last.Kind.ShouldBe(
            succeeds
                ? ConfiglueDiagnosticEventKind.MigrationCompleted
                : ConfiglueDiagnosticEventKind.MigrationFailed
        );
        last.SourceId.ShouldBe(SourceId.From("source"));
        var started = runtime
            .GetRecentEvents()
            .First(static item => item.Kind == ConfiglueDiagnosticEventKind.MigrationStarted);
        last.OperationId.ShouldBe(started.OperationId);
    }

    private sealed record DiagnosticSubject(string Name) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From(Name);
    }

    private sealed class DiagnosticFailingWriter(Exception exception)
        : ISourceWriter<AppSettings.Fragment>
    {
        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<AppSettings.Fragment> request,
            CancellationToken cancellationToken = default
        ) => throw exception;
    }
}
