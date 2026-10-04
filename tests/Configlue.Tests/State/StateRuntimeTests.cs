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
        var lowerPriority = new StateSource<string>("lower", new InMemoryStateSource<string>(), new StateSourceOptions<string> { Priority = 0 });
        var higherPriority = new StateSource<string>("higher", new InMemoryStateSource<string>(), new StateSourceOptions<string> { Priority = 10 });
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
                    new StateSource<AppSettings.Fragment>("first", first, new StateSourceOptions<AppSettings.Fragment> { Writer = first }),
                    new StateSource<AppSettings.Fragment>("second", second, new StateSourceOptions<AppSettings.Fragment> { Writer = second }),
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
                new StateSource<AppSettings.Fragment>("settings", reader, new StateSourceOptions<AppSettings.Fragment> { Writer = store }),
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
                new StateSource<AppSettings.Fragment>("settings", reader, new StateSourceOptions<AppSettings.Fragment> { Writer = store }),
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
    public async Task Resolver_FallsBackByPolicyAndWatchesHigherPrioritySourceForFailback()
    {
        var primary = new InMemoryStateSource<string>();
        primary.SetUnavailable();
        var fallback = new InMemoryStateSource<string>("local");
        var sources = new StateSourceSet<string>([
            new StateSource<string>("remote", primary, new StateSourceOptions<string> { Priority = 100, FallbackCondition = StateFallbackCondition.NotFoundOrUnavailable, Writer = primary, Watcher = primary }),
            new StateSource<string>("local", fallback, new StateSourceOptions<string> { Priority = 0, Writer = fallback, Watcher = fallback }),
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
            new StateSource<string>("pending", new FixedStateReader<string>(StateReadResult<string>.Unavailable("primary")), new StateSourceOptions<string> { Priority = 100, FallbackCondition = StateFallbackCondition.Unavailable, Watcher = pendingWatcher }),
            new StateSource<string>("throwing", new FixedStateReader<string>(
                    StateReadResult<string>.Success("fallback", "fallback")
                ), new StateSourceOptions<string> { Priority = 0, Watcher = throwingWatcher }),
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
            new StateSource<AppSettings.Fragment>("pending", new FixedStateReader<AppSettings.Fragment>(
                    StateReadResult<AppSettings.Fragment>.Unavailable("primary")
                ), new StateSourceOptions<AppSettings.Fragment> { Priority = 100, FallbackCondition = StateFallbackCondition.Unavailable, Watcher = pendingWatcher }),
            new StateSource<AppSettings.Fragment>("throwing", new FixedStateReader<AppSettings.Fragment>(
                    StateReadResult<AppSettings.Fragment>.Success(
                        new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) },
                        "fallback"
                    )
                ), new StateSourceOptions<AppSettings.Fragment> { Priority = 0, Watcher = throwingWatcher }),
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
    public async Task SerializedSource_ComposesResourceCodecWriterAndWatcherCapabilities()
    {
        var resource = new InMemoryResource();
        var source = new StateSource<AppSettings.Fragment>("serialized", new SerializedSource<AppSettings.Fragment>(resource, new JsonStateCodec<AppSettings.Fragment>(), writer: (IResourceReader)resource as IResourceWriter, watcher: (IResourceReader)resource as ISourceWatcher), new StateSourceOptions<AppSettings.Fragment> { PhysicalOrigin = "memory://settings" });
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
        var source = new StateSource<AppSettings.Fragment>("settings", new SerializedSource<AppSettings.Fragment>(resource, new JsonStateCodec<AppSettings.Fragment>(), writer: (IResourceReader)resource as IResourceWriter, watcher: (IResourceReader)resource as ISourceWatcher), new StateSourceOptions<AppSettings.Fragment>());
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
                    StateReadStatus.InvalidPayload => StateReadResult<T>.InvalidPayload(
                        value,
                        Revision
                    ),
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
