using System.Collections.Concurrent;
using Configlue.Resource.Redis;
using StackExchange.Redis;

namespace Configlue.Tests;

public sealed class RedisResourceCacheLifecycleTests
{
    // Generic disposal/materialization/eviction/concurrency semantics are owned by
    // core ResidencyCacheLeaseDisposeTests. This suite keeps the one case that is
    // provider-specific: route-to-connection resolution shares a single backend.
    [Test]
    public async Task ManyRoutesResolvingToOneConnectionRetainOneEntry()
    {
        var connection = new TrackingConnection();
        var factoryCalls = 0;
        var backend = new TrackingRedisStateBackend();
        using var resource = CreateResource(
            _ => connection,
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return backend;
            }
        );

        for (var index = 0; index < 128; index++)
        {
            await resource.WriteAsync(
                CreateContext($"tenant-{index}", RouteKey.From($"route-{index}")),
                new ResourceWriteRequest(new byte[] { 1 })
            );
        }

        Volatile.Read(ref factoryCalls).ShouldBe(1);
        resource.CachedBackendCount.ShouldBe(1);

        resource.Dispose();
        backend.DisposeCount.ShouldBe(1);
        connection.DisposeCount.ShouldBe(0);
    }

    private static RedisResource CreateResource(
        Func<RouteKey, object> connectionResolver,
        Func<object, IRedisStateBackend> backendFactory,
        RedisResourceOptions? options = null
    ) => new(connectionResolver, backendFactory, "settings", options);

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

    private sealed class TrackingConnection : IDisposable
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class TrackingRedisStateBackend : IRedisStateBackend
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<ResourceReadResult> ReadAsync(
            RedisResourceAddress address,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTaskCompat.FromResult(ResourceReadResult.NotFound());
        }

        public ValueTask<StateWriteResult> WriteAsync(
            RedisResourceAddress address,
            ResourceWriteRequest request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTaskCompat.FromResult(new StateWriteResult("1"));
        }

        public ValueTask WaitForChangeAsync(
            RedisResourceAddress address,
            string? observedRevision,
            CancellationToken cancellationToken
        ) => default;

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
