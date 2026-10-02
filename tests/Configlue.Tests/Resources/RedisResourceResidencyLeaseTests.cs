using Configlue.Resource.Redis;

namespace Configlue.Tests;

public sealed class RedisResourceResidencyLeaseTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposeDuringActiveOperationDefersBackendDisposalUntilCompletion(bool watch)
    {
        var operationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new BlockingRedisStateBackend(operationEntered, release);
        using var resource = new RedisResource(_ => new object(), _ => backend, "settings", null);

        var read = Task.Run(async () =>
        {
            if (watch)
                await resource.WaitForChangeAsync(
                    CreateContext("tenant-a", RouteKey.From("primary-a")),
                    null
                );
            else
                await resource.ReadAsync(CreateContext("tenant-a", RouteKey.From("primary-a")));
        });
        await operationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        resource.Dispose();
        backend.DisposeCount.ShouldBe(0);
        resource.CachedBackendCount.ShouldBe(0);

        release.TrySetResult();
        await read;
        backend.DisposeCount.ShouldBe(1);

        resource.Dispose();
        backend.DisposeCount.ShouldBe(1);
    }

    [Test]
    public async Task DisposeDuringActiveWriteDefersBackendDisposalUntilCompletion()
    {
        var operationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new BlockingRedisStateBackend(operationEntered, release);
        using var resource = new RedisResource(_ => new object(), _ => backend, "settings", null);

        var write = Task.Run(async () =>
            await resource.WriteAsync(
                CreateContext("tenant-a", RouteKey.From("primary-a")),
                new ResourceWriteRequest(new byte[] { 1 })
            )
        );
        await operationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        resource.Dispose();
        backend.DisposeCount.ShouldBe(0);

        release.TrySetResult();
        await write;
        backend.DisposeCount.ShouldBe(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposeCancelsWatchAndRetainsBackendThroughCleanup(bool callerCancels)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new BlockingRedisStateBackend(
            entered,
            release,
            observeWatchCancellation: true
        );
        using var owner = new RedisResource(_ => new object(), _ => backend, "settings", null);
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
        return new ConfiglueResourceContext(modelId, new FakeSubject(key), key, route);
    }

    private sealed record FakeSubject(SubjectKey Key) : IConfiglueSubject;

    private sealed class BlockingRedisStateBackend : IRedisStateBackend
    {
        private readonly TaskCompletionSource _entered;
        private readonly TaskCompletionSource _release;
        private int _disposeCount;
        private readonly bool _observeWatchCancellation;
        public TaskCompletionSource WatchCancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingRedisStateBackend(
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
            RedisResourceAddress address,
            CancellationToken cancellationToken
        )
        {
            _entered.TrySetResult();
            await _release.Task;
            return ResourceReadResult.NotFound();
        }

        public async ValueTask<StateWriteResult> WriteAsync(
            RedisResourceAddress address,
            ResourceWriteRequest request,
            CancellationToken cancellationToken
        )
        {
            _entered.TrySetResult();
            await _release.Task;
            return new StateWriteResult("1");
        }

        public async ValueTask WaitForChangeAsync(
            RedisResourceAddress address,
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
