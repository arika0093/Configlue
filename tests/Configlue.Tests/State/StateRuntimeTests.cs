using Configlue.Extensions.MSOptions;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Sources;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue.Tests;

[ConfiglueModel("replace-collection-settings", Version = 1)]
public partial class ReplaceCollectionSettings
{
    public IReadOnlyList<string> Values { get; set; } = [];
}

public sealed partial class StateRuntimeTests
{
    [Test]
    public void StateSourceSet_ExposesPriorityOrderedIndexedAccess()
    {
        var lowerPriority = new StateSource<string>(
            "lower",
            new InMemoryStateSource<string>(),
            priority: 0
        );
        var higherPriority = new StateSource<string>(
            "higher",
            new InMemoryStateSource<string>(),
            priority: 10
        );
        var sourceSet = new StateSourceSet<string>([lowerPriority, higherPriority]);

        sourceSet.Count.ShouldBe(2);
        sourceSet[0].Id.ShouldBe(SourceId.From("higher"));
        sourceSet[1].Id.ShouldBe(SourceId.From("lower"));
        sourceSet.Sources[0].Id.ShouldBe(SourceId.From("higher"));
    }

    [Test]
    public void ContextCreationRejectsAmbiguousWritableRootsWithoutADefaultOwner()
    {
        var first = new InMemoryStateSource<AppSettings.Fragment>();
        var second = new InMemoryStateSource<AppSettings.Fragment>();

        Should.Throw<InvalidOperationException>(() =>
            new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
                new StateSourceSet<AppSettings.Fragment>([
                    new StateSource<AppSettings.Fragment>("first", first, writer: first),
                    new StateSource<AppSettings.Fragment>("second", second, writer: second),
                ])
            )
        );
    }

    [Test]
    public async Task PatchSaveReusesResolvedBaselineForItsSourceRead()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("before") }
        );
        var reader = new CountingStateReader<AppSettings.Fragment>(store);
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("settings", reader, writer: store),
            ])
        );

        await options.SaveAsync(
            new AppSettings.Patch { Label = FragmentOperation<string?>.Set("after") }
        );

        (reader.ReadCount).ShouldBe(2);
        ((await store.ReadAsync()).Value!.Label.Value).ShouldBe("after");
    }

    [Test]
    public async Task PatchSaveStillChecksRevisionAtTheWriterAfterItsFinalRead()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("before") }
        );
        var reader = new CountingStateReader<AppSettings.Fragment>(
            store,
            afterRead: count =>
            {
                if (count == 2)
                {
                    store.Set(
                        new AppSettings.Fragment { Label = Optional<string?>.Present("concurrent") }
                    );
                }
            }
        );
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("settings", reader, writer: store),
            ])
        );

        await Should.ThrowAsync<StateConflictException>(async () =>
            await options.SaveAsync(
                new AppSettings.Patch { Label = FragmentOperation<string?>.Set("after") }
            )
        );

        (reader.ReadCount).ShouldBe(2);
        ((await store.ReadAsync()).Value!.Label.Value).ShouldBe("concurrent");
    }

    [Test]
    public async Task FallbackStateSource_UsesOneRepresentationAndWritesToTheSelectedCandidate()
    {
        var canonical = new InMemoryStateSource<string>();
        var legacy = new InMemoryStateSource<string>("legacy");
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
            new StateWriteRequest<string>(
                resolved.Value!,
                Condition: RevisionCondition.FromRevision(resolved.Revision)
            )
        );
        var legacyAfterWrite = await legacy.ReadAsync();
        var canonicalAfterWrite = await canonical.ReadAsync();

        (resolved.Status).ShouldBe(StateReadStatus.Success);
        (resolved.Value).ShouldBe("legacy");
        resolved.SourceId.ShouldBe(SourceId.From("legacy"));
        (resolved.PhysicalOrigin).ShouldBe("settings.yaml");
        (resolved.Revisions!.Revisions.Count).ShouldBe(2);
        (resolved.Revisions.NestedRevisions.Count).ShouldBe(0);
        (legacyAfterWrite.Value).ShouldBe("legacy");
        (canonicalAfterWrite.Status).ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public async Task FallbackStateSource_CanWriteToExplicitCanonicalCandidateWithoutLosingFallbackFields()
    {
        var canonical = new InMemoryStateSource<AppSettings.Fragment>();
        var legacy = new InMemoryStateSource<AppSettings.Fragment>(
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
            writeSourceId: SourceId.From("canonical")
        );
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([fallback.CreateSource("settings")]),
            onChangeDebounce: TimeSpan.Zero
        );

        var initial = await options.ReadAsync();
        await options.SaveAsync(settings => settings.Label = "canonical");
        var canonicalAfterWrite = await canonical.ReadAsync();
        var legacyAfterWrite = await legacy.ReadAsync();
        var resolvedAfterWrite = await options.ReadAsync();
        var selectedAfterWrite = await fallback.ReadAsync();

        (initial.Value!.RetryCount).ShouldBe(11);
        (initial.Value.Label).ShouldBe("legacy");
        (canonicalAfterWrite.Value!.RetryCount.Value).ShouldBe(11);
        (canonicalAfterWrite.Value.Label.Value).ShouldBe("canonical");
        (legacyAfterWrite.Value!.RetryCount.Value).ShouldBe(11);
        (legacyAfterWrite.Value.Label.Value).ShouldBe("legacy");
        resolvedAfterWrite.SourceId.ShouldBe(SourceId.From("settings"));
        (resolvedAfterWrite.Value!.RetryCount).ShouldBe(11);
        (resolvedAfterWrite.Value.Label).ShouldBe("canonical");
        selectedAfterWrite.SourceId.ShouldBe(SourceId.From("canonical"));
    }

    [Test]
    public async Task FallbackStateSource_CanExplicitlyMaterializeSelectedValueToCanonicalCandidate()
    {
        var canonical = new InMemoryStateSource<string>();
        var legacy = new InMemoryStateSource<string>("legacy");
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
            ]),
            writeSourceId: SourceId.From("canonical")
        );

        var snapshot = await fallback.ReadAsync();
        var canonicalBeforeWrite = await canonical.ReadAsync();
        var legacyBeforeWrite = await legacy.ReadAsync();

        (canonicalBeforeWrite.Status).ShouldBe(StateReadStatus.NotFound);
        (legacyBeforeWrite.Value).ShouldBe("legacy");

        await fallback.WriteAsync(
            new StateWriteRequest<string>(
                snapshot.Value!,
                Condition: RevisionCondition.FromRevision(snapshot.Revision)
            )
        );

        var canonicalAfterWrite = await canonical.ReadAsync();
        var legacyAfterWrite = await legacy.ReadAsync();
        var resolvedAfterWrite = await fallback.ReadAsync();

        (canonicalAfterWrite.Value).ShouldBe("legacy");
        (legacyAfterWrite.Value).ShouldBe("legacy");
        resolvedAfterWrite.SourceId.ShouldBe(SourceId.From("canonical"));
        (resolvedAfterWrite.Value).ShouldBe("legacy");
    }

    [Test]
    public async Task FallbackStateSource_WatchesForFailbackButIgnoresLowerPriorityChangesAfterSelection()
    {
        var canonical = new InMemoryStateSource<string>();
        var legacy = new InMemoryStateSource<string>("legacy");
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

        initial.SourceId.ShouldBe(SourceId.From("legacy"));
        recovered.SourceId.ShouldBe(SourceId.From("canonical"));
        (lowerPriorityChangeWasIgnored).ShouldBeTrue();
    }

    [Test]
    public async Task FallbackStateSource_RejectsWriteWhenSelectedRepresentationChangedAfterRead()
    {
        var canonical = new InMemoryStateSource<string>();
        var legacy = new InMemoryStateSource<string>("legacy");
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
                new StateWriteRequest<string>(
                    "stale write",
                    Condition: RevisionCondition.FromRevision(initial.Revision)
                )
            )
        );

        ((await legacy.ReadAsync()).Value).ShouldBe("changed");
    }

    [Test]
    public async Task Resolver_FallsBackByPolicyAndWatchesHigherPrioritySourceForFailback()
    {
        var primary = new InMemoryStateSource<string>();
        primary.SetUnavailable();
        var fallback = new InMemoryStateSource<string>("local");
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
        var reader = new StateSourceResolver<string>(sources);
        var writer = new StateSourceWriter<string>(sources, SourceId.From("local"));
        var watcher = new StateSourceWatcher<string>(reader);

        var resolved = await reader.ReadAsync();
        await writer.WriteAsync(
            new StateWriteRequest<string>(
                "edited locally",
                Condition: RevisionCondition.FromRevision(resolved.Revision)
            )
        );
        var localAfterWrite = await fallback.ReadAsync();
        var failbackWait = watcher.WaitForChangeAsync(localAfterWrite.Revision).AsTask();
        primary.Set("remote");
        await failbackWait;
        var recovered = await reader.ReadAsync();

        (resolved.Value).ShouldBe("local");
        resolved.SourceId.ShouldBe(SourceId.From("local"));
        (resolved.Revisions!.Revisions.Count).ShouldBe(2);
        (localAfterWrite.Value).ShouldBe("edited locally");
        (recovered.Value).ShouldBe("remote");
        recovered.SourceId.ShouldBe(SourceId.From("remote"));
        reader.ActiveSource!.Id.ShouldBe(SourceId.From("remote"));
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
        // The candidate set is read-only, so only the reader/watcher pair is composed here:
        // StateSourceWriter construction now rejects sets without a writable root.
        var reader = new StateSourceResolver<string>(sources);
        var watcher = new StateSourceWatcher<string>(reader);
        await reader.ReadAsync();

        var threw = false;
        try
        {
            await watcher.WaitForChangeAsync("fallback");
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
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
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
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(sourceSet);

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

    [Test]
    public async Task RuntimeStampsGeneratedModelIdOnPhysicalResourceContext()
    {
        var resource = new CapturingResource();
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "settings",
            resource,
            new JsonStateCodec<AppSettings.Fragment>()
        );
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([source])
        );

        try
        {
            _ = await options.GetValueAsync();
        }
        catch (InvalidOperationException)
        {
            // The capturing resource reports NotFound; only the context is under test.
        }

        resource.LastContext.HasValue.ShouldBeTrue();
        resource.LastContext!.Value.ModelId.ShouldBe(AppSettings.ConfiglueSchema.Id);
    }

    private sealed class CapturingResource : IResourceReader
    {
        public ConfiglueResourceContext? LastContext { get; private set; }

        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastContext = context;
            return ValueTaskCompat.FromResult(ResourceReadResult.NotFound());
        }
    }

    private sealed class FixedStateReader<T>(StateReadResult<T> result) : ISourceReader<T>
    {
        public ValueTask<StateReadResult<T>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTaskCompat.FromResult(result);
        }
    }

    private sealed class OpaqueRevisionStateStore<T> : ISourceReader<T>, ISourceWatcher
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
            _result = (
                status switch
                {
                    StateReadStatus.Success => StateReadResult<T>.Success(value!, Revision),
                    StateReadStatus.InvalidPayload => StateReadResult<T>.InvalidPayload(value, Revision),
                    StateReadStatus.Unavailable => StateReadResult<T>.Unavailable(Revision),
                    _ => StateReadResult<T>.NotFound(Revision),
                }
            ) with
            {
                PhysicalOrigin = physicalOrigin,
            };

        public Task WatchStarted => _watchStarted.Task;

        public ValueTask<StateReadResult<T>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return ValueTaskCompat.FromResult(_result);
            }
        }

        public async ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
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
                _result = StateReadResult<T>.Success(value!, Revision) with
                {
                    PhysicalOrigin = physicalOrigin,
                };
                changed = _changed;
                _changed = NewSignal();
            }

            changed.TrySetResult();
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PendingStateWatcher : ISourceWatcher
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
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            _started.TrySetResult(true);
            _ = cancellationToken.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                _cancellationObserved
            );
            return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        }
    }

    private sealed class SynchronousThrowingStateWatcher : ISourceWatcher
    {
        public ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Simulated synchronous watcher startup failure.");
    }

    private sealed class FailOnceStateWriter<T>(ISourceWriter<T> inner) : ISourceWriter<T>
    {
        private int _shouldFail = 1;

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<T> request,
            CancellationToken cancellationToken = default
        )
        {
            if (Interlocked.Exchange(ref _shouldFail, 0) == 1)
            {
                throw new IOException("Simulated transient target failure.");
            }

            return inner.WriteAsync(context, request, cancellationToken);
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
            return ValueTaskCompat.FromResult(builder.Build());
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
            return ValueTaskCompat.FromResult(builder.Build());
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
        IWritableState<AppSettings> options
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

    private sealed class CountingStateReader<T>(
        ISourceReader<T> inner,
        Action<int>? afterRead = null
    ) : ISourceReader<T>
    {
        public int ReadCount { get; private set; }

        public async ValueTask<StateReadResult<T>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            var result = await inner.ReadAsync(context, cancellationToken).ConfigureAwait(false);
            var readCount = ++ReadCount;
            afterRead?.Invoke(readCount);
            return result;
        }
    }
}
