using System.Collections.Concurrent;
using Configlue.Resource.Redis;
using StackExchange.Redis;

namespace Configlue.Tests;

public sealed class RedisResourceCacheLifecycleTests
{
    [Test]
    public async Task DisposalWhileMaterializerBlockedDisposesLateBackendExactlyOnce()
    {
        var factoryEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new TrackingRedisStateBackend();
        using var resource = CreateResource(
            _ => new object(),
            _ =>
            {
                factoryEntered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return backend;
            }
        );

        var read = Task.Run(async () =>
            await resource.ReadAsync(CreateContext("tenant-a", RouteKey.From("primary-a")))
        );
        await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        resource.Dispose();
        release.TrySetResult();

        await Should.ThrowAsync<ObjectDisposedException>(async () => await read);
        backend.DisposeCount.ShouldBe(1);
        resource.CachedBackendCount.ShouldBe(0);

        resource.Dispose();
        backend.DisposeCount.ShouldBe(1);
    }

    [Test]
    public async Task DisposalWhileResolverBlockedNeverMaterializesBackend()
    {
        var resolverEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        using var resource = CreateResource(
            _ =>
            {
                resolverEntered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return new object();
            },
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new TrackingRedisStateBackend();
            }
        );

        var read = Task.Run(async () =>
            await resource.ReadAsync(CreateContext("tenant-a", RouteKey.From("primary-a")))
        );
        await resolverEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        resource.Dispose();
        release.TrySetResult();

        await Should.ThrowAsync<ObjectDisposedException>(async () => await read);
        Volatile.Read(ref factoryCalls).ShouldBe(0);
        resource.CachedBackendCount.ShouldBe(0);
    }

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

    [Test]
    public async Task ManyPhysicalConnectionsAreEvictedToCapacity()
    {
        var created = new ConcurrentQueue<TrackingRedisStateBackend>();
        var connections = new ConcurrentDictionary<RouteKey, object>();
        using var resource = CreateResource(
            route => connections.GetOrAdd(route, static _ => new object()),
            _ =>
            {
                var backend = new TrackingRedisStateBackend();
                created.Enqueue(backend);
                return backend;
            },
            new RedisResourceOptions { BackendCacheCapacity = 2 }
        );

        foreach (var index in Enumerable.Range(0, 3))
        {
            await resource.WriteAsync(
                CreateContext($"tenant-{index}", RouteKey.From($"route-{index}")),
                new ResourceWriteRequest(new byte[] { 1 })
            );
        }

        created.Count.ShouldBe(3);
        created.Count(static backend => backend.DisposeCount == 1).ShouldBe(1);
        resource.CachedBackendCount.ShouldBe(2);

        resource.TrimBackendCache();
        resource.Dispose();
        created.ShouldAllBe(static backend => backend.DisposeCount == 1);
        resource.CachedBackendCount.ShouldBe(0);
    }

    [Test]
    public async Task ConcurrentAccessDuringEvictionAndDisposalIsSafe()
    {
        var created = new ConcurrentQueue<TrackingRedisStateBackend>();
        var resource = CreateResource(
            _ => new object(),
            _ =>
            {
                var backend = new TrackingRedisStateBackend();
                created.Enqueue(backend);
                return backend;
            },
            new RedisResourceOptions
            {
                BackendCacheCapacity = 4,
                BackendCacheIdleTimeout = TimeSpan.Zero,
            }
        );

        var workers = Enumerable
            .Range(0, 24)
            .Select(worker =>
                Task.Run(async () =>
                {
                    for (var index = 0; index < 40; index++)
                    {
                        await resource.WriteAsync(
                            CreateContext($"t{worker}", RouteKey.From($"r-{worker}-{index}")),
                            new ResourceWriteRequest(new byte[] { 1 })
                        );
                    }
                })
            )
            .ToArray();
        var trimmer = Task.Run(() =>
        {
            for (var index = 0; index < 200; index++)
            {
                resource.TrimBackendCache();
            }
        });

        await Task.WhenAll(workers.Append(trimmer));
        resource.Dispose();

        resource.CachedBackendCount.ShouldBe(0);
        created.ShouldNotBeEmpty();
        created.ShouldAllBe(static backend => backend.DisposeCount == 1);
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
