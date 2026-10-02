using System.Collections.Concurrent;
using Configlue.Provider.Json;
using Configlue.Source.PostgreSql;

namespace Configlue.Tests;

public sealed class PostgreSqlSourceCacheLifecycleTests
{
    [Test]
    public async Task DisposalWhileMaterializerBlockedDisposesLateBackendExactlyOnce()
    {
        var factoryEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new TrackingPostgreSqlStateBackend();
        using var source = CreateSource(
            _ => new object(),
            _ =>
            {
                factoryEntered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return backend;
            }
        );

        var read = Task.Run(async () =>
            await source.ReadAsync(CreateContext("tenant-a", RouteKey.From("region-a")))
        );
        await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        source.Dispose();
        release.TrySetResult();

        await Should.ThrowAsync<ObjectDisposedException>(async () => await read);
        backend.DisposeCount.ShouldBe(1);
        source.CachedBackendCount.ShouldBe(0);

        source.Dispose();
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
        using var source = CreateSource(
            _ =>
            {
                resolverEntered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return new object();
            },
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new TrackingPostgreSqlStateBackend();
            }
        );

        var read = Task.Run(async () =>
            await source.ReadAsync(CreateContext("tenant-a", RouteKey.From("region-a")))
        );
        await resolverEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        source.Dispose();
        release.TrySetResult();

        await Should.ThrowAsync<ObjectDisposedException>(async () => await read);
        Volatile.Read(ref factoryCalls).ShouldBe(0);
        source.CachedBackendCount.ShouldBe(0);
    }

    [Test]
    public async Task ManyRoutesResolvingToOneConnectionRetainOneEntry()
    {
        var connection = new TrackingConnection();
        var factoryCalls = 0;
        var backend = new TrackingPostgreSqlStateBackend();
        using var source = CreateSource(
            _ => connection,
            _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                return backend;
            }
        );

        for (var index = 0; index < 128; index++)
        {
            await source.WriteAsync(
                CreateContext($"tenant-{index}", RouteKey.From($"region-{index}")),
                new StateWriteRequest<string>("1")
            );
        }

        Volatile.Read(ref factoryCalls).ShouldBe(1);
        source.CachedBackendCount.ShouldBe(1);

        source.Dispose();
        backend.DisposeCount.ShouldBe(1);
        connection.DisposeCount.ShouldBe(0);
    }

    [Test]
    public async Task ManyPhysicalConnectionsAreEvictedToCapacity()
    {
        var created = new ConcurrentQueue<TrackingPostgreSqlStateBackend>();
        var connections = new ConcurrentDictionary<RouteKey, object>();
        using var source = CreateSource(
            route => connections.GetOrAdd(route, static _ => new object()),
            _ =>
            {
                var backend = new TrackingPostgreSqlStateBackend();
                created.Enqueue(backend);
                return backend;
            },
            new PostgreSqlTableOptions { BackendCacheCapacity = 2 }
        );

        foreach (var index in Enumerable.Range(0, 3))
        {
            await source.WriteAsync(
                CreateContext($"tenant-{index}", RouteKey.From($"region-{index}")),
                new StateWriteRequest<string>("1")
            );
        }

        created.Count.ShouldBe(3);
        created.Count(static backend => backend.DisposeCount == 1).ShouldBe(1);
        source.CachedBackendCount.ShouldBe(2);

        source.TrimBackendCache();
        source.Dispose();
        created.ShouldAllBe(static backend => backend.DisposeCount == 1);
        source.CachedBackendCount.ShouldBe(0);
    }

    [Test]
    public async Task ConcurrentAccessDuringEvictionAndDisposalIsSafe()
    {
        var created = new ConcurrentQueue<TrackingPostgreSqlStateBackend>();
        var source = CreateSource(
            _ => new object(),
            _ =>
            {
                var backend = new TrackingPostgreSqlStateBackend();
                created.Enqueue(backend);
                return backend;
            },
            new PostgreSqlTableOptions
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
                        await source.WriteAsync(
                            CreateContext($"t{worker}", RouteKey.From($"r-{worker}-{index}")),
                            new StateWriteRequest<string>("1")
                        );
                    }
                })
            )
            .ToArray();
        var trimmer = Task.Run(() =>
        {
            for (var index = 0; index < 200; index++)
            {
                source.TrimBackendCache();
            }
        });

        await Task.WhenAll(workers.Append(trimmer));
        source.Dispose();

        source.CachedBackendCount.ShouldBe(0);
        created.ShouldNotBeEmpty();
        created.ShouldAllBe(static backend => backend.DisposeCount == 1);
    }

    private static PostgreSqlSource<string> CreateSource(
        Func<RouteKey, object> connectionResolver,
        Func<object, IPostgreSqlStateBackend> backendFactory,
        PostgreSqlTableOptions? tableOptions = null
    ) =>
        new(
            connectionResolver,
            backendFactory,
            "settings",
            new JsonStateValueSerializer<string>(),
            tableOptions
        );

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

    private sealed class TrackingConnection : IDisposable
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class TrackingPostgreSqlStateBackend : IPostgreSqlStateBackend
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<ResourceReadResult> ReadAsync(
            string resourceNamespace,
            string modelId,
            string subjectKey,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTaskCompat.FromResult(ResourceReadResult.NotFound());
        }

        public ValueTask<StateWriteResult> WriteAsync(
            string resourceNamespace,
            string modelId,
            string subjectKey,
            ResourceWriteRequest request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTaskCompat.FromResult(new StateWriteResult("1"));
        }

        public ValueTask WaitForChangeAsync(
            string resourceNamespace,
            string modelId,
            string subjectKey,
            string? observedRevision,
            CancellationToken cancellationToken
        ) => default;

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
