using System.Collections.Concurrent;
using Configlue.Provider.Json;
using Configlue.Source.PostgreSql;

namespace Configlue.Tests;

public sealed class PostgreSqlSourceCacheLifecycleTests
{
    // Generic disposal/materialization/eviction/concurrency semantics are owned by
    // core ResidencyCacheLeaseDisposeTests. This suite keeps the one case that is
    // provider-specific: route-to-connection resolution shares a single backend.
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
        return new ConfiglueResourceContext(modelId, new FakeSubject(key), ResourceKey.From(key), route);
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
