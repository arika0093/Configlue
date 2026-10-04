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
        await using var runtime = CreateRuntime([
            new StateSource<AppSettings.Fragment>(
                "store",
                store,
                new StateSourceOptions<AppSettings.Fragment> { Writer = store }
            ),
        ]);
        await runtime.SaveAsync(
            new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }
        );
        var last = runtime.GetRuntimeSnapshot().LastWrite!.Value;
        last.Kind.ShouldBe(ConfiglueDiagnosticEventKind.WriteCompleted);
        last.SourceId.ShouldBe(SourceId.From("store"));
        last.HasRevision.ShouldBeTrue();
        last.Duration.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
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
        await using var runtime = CreateRuntime([
            new StateSource<AppSettings.Fragment>(
                "store",
                store,
                new StateSourceOptions<AppSettings.Fragment>
                {
                    Writer = new DiagnosticFailingWriter(exception),
                }
            ),
        ]);
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
        System
            .Text.Json.JsonSerializer.Serialize(runtime.GetRuntimeSnapshot())
            .ShouldNotContain("secret-");
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
            StateWritePlan.DefaultTo(SourceId.From("first"))
        );
        await runtime.ApplyPatchesAsync([
            new StateSourcePatch(
                SourceId.From("first"),
                new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }
            ),
            new StateSourcePatch(
                SourceId.From("second"),
                new AppSettings.Patch { Label = FragmentOperation<string?>.Set("section-secret") }
            ),
        ]);
        resource.WriteCount.ShouldBe(1);
        var last = runtime.GetRuntimeSnapshot().LastWrite!.Value;
        last.Kind.ShouldBe(ConfiglueDiagnosticEventKind.WriteCompleted);
        last.HasRevision.ShouldBeTrue();
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
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "store",
                    store,
                    new StateSourceOptions<AppSettings.Fragment> { Watcher = store }
                ),
            ]),
            onChangeDebounce: TimeSpan.Zero
        );
        using var subscription = runtime.OnChange(static _ => { });
        await WaitForWatchingAsync(runtime, true);
        store.Set(new AppSettings.Fragment { RetryCount = changeValue ? 7 : 3 });
        var completed = await WaitForReloadAsync(runtime);
        completed.EffectiveValueChanged.ShouldBe(changeValue);
        completed.Kind.ShouldBe(ConfiglueDiagnosticEventKind.ReloadCompleted);
        runtime.GetRuntimeSnapshot().LastReload!.Value.ShouldBe(completed);
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
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "store",
                    store,
                    new StateSourceOptions<AppSettings.Fragment> { Watcher = store }
                ),
            ]),
            onChangeDebounce: TimeSpan.Zero
        );
        using var subscription = runtime.OnChange(static _ => { });
        await WaitForWatchingAsync(runtime, true);
        store.SetUnavailable();
        var last = await WaitForReloadAsync(runtime);
        last.Kind.ShouldBe(ConfiglueDiagnosticEventKind.ReloadFailed);
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
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "store",
                    store,
                    new StateSourceOptions<AppSettings.Fragment> { Watcher = store }
                ),
            ])
        );
        using var first = runtime
            .ForSubject(new DiagnosticSubject("secret-first"))
            .OnChange(static _ => { });
        using var second = runtime
            .ForSubject(new DiagnosticSubject("secret-second"))
            .OnChange(static _ => { });
        await WaitForWatchingAsync(runtime, true);
        first.Dispose();
        // One watch remains active; the source still reports watching.
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
        await using var runtime = CreateRuntime([
            new StateSource<AppSettings.Fragment>(
                "source",
                source,
                new StateSourceOptions<AppSettings.Fragment>()
            ),
            new StateSource<AppSettings.Fragment>(
                "target",
                target,
                new StateSourceOptions<AppSettings.Fragment> { Writer = target }
            ),
        ]);
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
    }

    private static async Task WaitForWatchingAsync(
        ConfiglueRuntime<AppSettings, AppSettings.Fragment> runtime,
        bool expected,
        int timeoutMilliseconds = 5000
    )
    {
        var elapsed = 0;
        while (runtime.GetRuntimeSnapshot().Sources.Single().IsWatching != expected)
        {
            if (elapsed >= timeoutMilliseconds)
                throw new TimeoutException("Timed out waiting for the watch state to settle.");
            await Task.Delay(20);
            elapsed += 20;
        }
    }

    private static async Task<ConfiglueDiagnosticEvent> WaitForReloadAsync(
        ConfiglueRuntime<AppSettings, AppSettings.Fragment> runtime,
        int timeoutMilliseconds = 5000
    )
    {
        var elapsed = 0;
        while (runtime.GetRuntimeSnapshot().LastReload is null)
        {
            if (elapsed >= timeoutMilliseconds)
                throw new TimeoutException("Timed out waiting for a reload outcome.");
            await Task.Delay(20);
            elapsed += 20;
        }
        return runtime.GetRuntimeSnapshot().LastReload!.Value;
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
