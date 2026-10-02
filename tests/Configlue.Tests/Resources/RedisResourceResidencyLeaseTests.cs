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

        public BlockingRedisStateBackend(TaskCompletionSource entered, TaskCompletionSource release)
        {
            _entered = entered;
            _release = release;
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
            await _release.Task;
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
