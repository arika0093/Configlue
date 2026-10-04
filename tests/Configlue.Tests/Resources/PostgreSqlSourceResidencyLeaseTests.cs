using Configlue.Provider.Json;
using Configlue.Source.PostgreSql;

namespace Configlue.Tests;

public sealed class PostgreSqlSourceResidencyLeaseTests
{
    // Read, write, and watch share the same AcquireBackend lease, so this
    // parameterized read/watch case owns deferred-disposal wiring for all operations.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposeDuringActiveOperationDefersBackendDisposalUntilCompletion(bool watch)
    {
        var operationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new BlockingPostgreSqlStateBackend(operationEntered, release);
        using var source = new PostgreSqlSource<string>(
            _ => new object(),
            _ => backend,
            "settings",
            new JsonStateValueSerializer<string>()
        );

        var read = Task.Run(async () =>
        {
            if (watch)
                await source.WaitForChangeAsync(
                    CreateContext("tenant-a", RouteKey.From("region-a")),
                    null
                );
            else
                await source.ReadAsync(CreateContext("tenant-a", RouteKey.From("region-a")));
        });
        await operationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        source.Dispose();
        backend.DisposeCount.ShouldBe(0);
        source.CachedBackendCount.ShouldBe(0);

        release.TrySetResult();
        await read;
        backend.DisposeCount.ShouldBe(1);

        source.Dispose();
        backend.DisposeCount.ShouldBe(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposeCancelsWatchAndRetainsBackendThroughCleanup(bool callerCancels)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new BlockingPostgreSqlStateBackend(
            entered,
            release,
            observeWatchCancellation: true
        );
        using var owner = new PostgreSqlSource<string>(
            _ => new object(),
            _ => backend,
            "settings",
            new JsonStateValueSerializer<string>()
        );
        using var cancellation = new CancellationTokenSource();
        var watch = owner
            .WaitForChangeAsync(CreateContext("tenant"), null, cancellation.Token)
            .AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        owner.Dispose();
        await backend.WatchCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        backend.DisposeCount.ShouldBe(0);
        watch.IsCompleted.ShouldBeFalse();
        if (callerCancels)
            cancellation.Cancel();
        release.TrySetResult();
        if (callerCancels)
        {
            var exception = await Should.ThrowAsync<OperationCanceledException>(async () =>
                await watch.WaitAsync(TimeSpan.FromSeconds(5))
            );
            exception.CancellationToken.ShouldBe(cancellation.Token);
        }
        else
            await watch.WaitAsync(TimeSpan.FromSeconds(5));
        backend.DisposeCount.ShouldBe(1);
    }

    private static ConfiglueResourceContext CreateContext(
        string subject,
        RouteKey route = default,
        string? modelId = null
    )
    {
        var key = SubjectKey.From(subject);
        return new ConfiglueResourceContext(modelId, new FakeSubject(key), ResourceKey.From(key), route);
    }

    private sealed record FakeSubject(SubjectKey Key) : IConfiglueSubject;

    private sealed class BlockingPostgreSqlStateBackend : IPostgreSqlStateBackend
    {
        private readonly TaskCompletionSource _entered;
        private readonly TaskCompletionSource _release;
        private int _disposeCount;
        private readonly bool _observeWatchCancellation;
        public TaskCompletionSource WatchCancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingPostgreSqlStateBackend(
            TaskCompletionSource entered,
            TaskCompletionSource release,
            bool observeWatchCancellation = false
        )
        {
            _entered = entered;
            _release = release;
            _observeWatchCancellation = observeWatchCancellation;
        }

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public async ValueTask<ResourceReadResult> ReadAsync(
            string resourceNamespace,
            string modelId,
            string subjectKey,
            CancellationToken cancellationToken
        )
        {
            _entered.TrySetResult();
            await _release.Task;
            return ResourceReadResult.NotFound();
        }

        public async ValueTask<StateWriteResult> WriteAsync(
            string resourceNamespace,
            string modelId,
            string subjectKey,
            ResourceWriteRequest request,
            CancellationToken cancellationToken
        )
        {
            _entered.TrySetResult();
            await _release.Task;
            return new StateWriteResult("1");
        }

        public async ValueTask WaitForChangeAsync(
            string resourceNamespace,
            string modelId,
            string subjectKey,
            string? observedRevision,
            CancellationToken cancellationToken
        )
        {
            _entered.TrySetResult();
            if (_observeWatchCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    WatchCancellationObserved.TrySetResult();
                    await _release.Task;
                    throw;
                }
            }
            await _release.Task;
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
