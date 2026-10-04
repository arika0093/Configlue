using System.Security.Cryptography;
using System.Text;
using Configlue.Sources;

namespace Configlue.Resource.Etcd;

/// <summary>
/// Reads and writes one serialized byte resource stored at a single etcd key.
/// Revisions are etcd modification revisions used for compare-and-swap writes.
/// Serialized sources reuse this resource through the normal Resource + Codec
/// composition path instead of duplicating codec logic.
/// </summary>
public sealed class EtcdResource
    : IResourceReader,
        IResourceBatchWriter,
        IResourceBatchCompatibility,
        ISourceWatcher,
        IDisposable
{
    private readonly Func<RouteKey, IEtcdClient> _clientResolver;
    private readonly EtcdResourceOptions _options;
    private readonly EtcdWatchShutdown _watchShutdown = new();
    private int _disposed;

    /// <summary>Creates a resource that uses one shared caller-owned etcd client.</summary>
    /// <param name="client">The caller-owned etcd client used for every route.</param>
    /// <param name="options">Key prefix, endpoint, and transport settings.</param>
    public EtcdResource(IEtcdClient client, EtcdResourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _clientResolver = _ => client;
        _options = options ?? new EtcdResourceOptions();
        _options.Validate();
    }

    /// <summary>Creates a resource that resolves a shared caller-owned etcd client per route.</summary>
    /// <param name="clientResolver">Resolves the caller-owned etcd client for each route.</param>
    /// <param name="options">Key prefix, endpoint, and transport settings.</param>
    public EtcdResource(
        Func<RouteKey, IEtcdClient> clientResolver,
        EtcdResourceOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(clientResolver);
        _clientResolver = route =>
            clientResolver(route)
            ?? throw new InvalidOperationException("The etcd client resolver returned null.");
        _options = options ?? new EtcdResourceOptions();
        _options.Validate();
    }

    internal EtcdResource(
        Func<RouteKey, IEtcdKvClient> kvResolver,
        Func<RouteKey, IEtcdWatcherClient> watcherResolver,
        EtcdResourceOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(kvResolver);
        ArgumentNullException.ThrowIfNull(watcherResolver);
        _clientResolver = route =>
        {
            var kv =
                kvResolver(route)
                ?? throw new InvalidOperationException("The etcd client resolver returned null.");
            var watcher =
                watcherResolver(route)
                ?? throw new InvalidOperationException("The etcd client resolver returned null.");
            return new CombinedEtcdClient(kv, watcher);
        };
        _options = options ?? new EtcdResourceOptions();
        _options.Validate();
    }

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context)
    {
        if (_options.FixedResourceId is { } explicitId)
        {
            return explicitId;
        }

        var address = ResolveAddress(context);
        return new ResourceId("etcd:" + CreateIdentityHash(address.Key));
    }

    /// <inheritdoc />
    public object? GetBatchCompatibilityToken(ConfiglueResourceContext context)
    {
        _ = context;
        var prefix = _options.KeyPrefixSelector is null
            ? EtcdKeyEncoding.NormalizePrefix(_options.KeyPrefix)
            : "selector";
        return "etcd:" + prefix + "|endpoints:" + string.Join(",", _options.Endpoints);
    }

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var client = ResolveClient(context);
        var address = ResolveAddress(context);
        cancellationToken.ThrowIfCancellationRequested();
        var response = await client
            .GetPrefixAsync(address.Key, null, cancellationToken)
            .ConfigureAwait(false);
        var match = response.Kvs.FirstOrDefault(kv =>
            string.Equals(kv.Key, address.Key, StringComparison.Ordinal)
        );
        if (match is not null)
        {
            var revision = EtcdRevisionCodec.Encode(
                response.HeaderRevision,
                new Dictionary<string, long>(StringComparer.Ordinal)
                {
                    [match.Key] = match.ModRevision,
                }
            );
            return ResourceReadResult.Success(match.Value.ToArray(), revision);
        }

        return ResourceReadResult.NotFound(
            response.HeaderRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ":"
        );
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var client = ResolveClient(context);
        var address = ResolveAddress(context);
        var backend = new EtcdStateBackend(client, client, _options);
        try
        {
            var (compares, writes) = PrepareSingleWrite(address.Key, request);
            var response = await backend
                .TransactAsync(compares, writes, cancellationToken)
                .ConfigureAwait(false);
            if (!response.Succeeded)
            {
                throw CreateConflict(address.Key);
            }

            return new StateWriteResult(
                EtcdRevisionCodec.Encode(
                    response.HeaderRevision,
                    new Dictionary<string, long>(StringComparer.Ordinal)
                    {
                        [address.Key] = response.HeaderRevision,
                    }
                )
            );
        }
        finally
        {
            backend.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteBatchAsync(
        IReadOnlyList<ResourceWriteMutation> mutations,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ResourceWriteMutation.ValidateBatch(mutations);
        _ = ResourceWriteMutation.ResolveBatchSchema(mutations);

        var first = mutations[0];
        var context = first.Context;
        var client = ResolveClient(context);
        var address = ResolveAddress(context);
        for (var index = 1; index < mutations.Count; index++)
        {
            if (
                !string.Equals(
                    ResolveAddress(mutations[index].Context).Key,
                    address.Key,
                    StringComparison.Ordinal
                )
            )
            {
                throw new NotSupportedException("etcd batch mutations address different keys.");
            }
        }

        var backend = new EtcdStateBackend(client, client, _options);
        try
        {
            var response = await backend
                .GetPrefixAsync(address.Key, null, cancellationToken)
                .ConfigureAwait(false);
            var match = response.Kvs.FirstOrDefault(kv =>
                string.Equals(kv.Key, address.Key, StringComparison.Ordinal)
            );
            ResourceReadResult current = ResourceReadResult.NotFound();
            long? modRevision = null;
            if (match is not null)
            {
                current = ResourceReadResult.Success(
                    match.Value.ToArray(),
                    EtcdRevisionCodec.Encode(
                        response.HeaderRevision,
                        new Dictionary<string, long>(StringComparer.Ordinal)
                        {
                            [match.Key] = match.ModRevision,
                        }
                    )
                );
                modRevision = match.ModRevision;
            }

            if (!IsSatisfied(first.Condition, address.Key, modRevision))
            {
                throw CreateConflict(address.Key);
            }

            foreach (var mutation in mutations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = mutation.Apply(current).ToArray();
                current = ResourceReadResult.Success(content, current.Revision);
            }

            var (compares, writes) = PrepareSingleWrite(
                address.Key,
                new ResourceWriteRequest(current.Content, first.Condition)
            );
            var result = await backend
                .TransactAsync(compares, writes, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw CreateConflict(address.Key);
            }

            return new StateWriteResult(
                EtcdRevisionCodec.Encode(
                    result.HeaderRevision,
                    new Dictionary<string, long>(StringComparer.Ordinal)
                    {
                        [address.Key] = result.HeaderRevision,
                    }
                )
            );
        }
        finally
        {
            backend.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        var client = ResolveClient(context);
        var address = ResolveAddress(context);
        var backend = new EtcdStateBackend(client, client, _options);
        try
        {
            await _watchShutdown
                .WaitAsync(
                    watchCancellationToken =>
                        backend.WaitForChangeAsync(
                            address.WatchPrefix,
                            observedRevision,
                            token => ReadRevisionAsync(backend, address, token),
                            watchCancellationToken
                        ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        finally
        {
            backend.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _watchShutdown.Signal();
    }

    private static async ValueTask<string?> ReadRevisionAsync(
        EtcdStateBackend backend,
        EtcdAddress address,
        CancellationToken cancellationToken
    )
    {
        var response = await backend
            .GetPrefixAsync(address.Key, null, cancellationToken)
            .ConfigureAwait(false);
        var match = response.Kvs.FirstOrDefault(kv =>
            string.Equals(kv.Key, address.Key, StringComparison.Ordinal)
        );
        if (match is not null)
        {
            return EtcdRevisionCodec.Encode(
                response.HeaderRevision,
                new Dictionary<string, long>(StringComparer.Ordinal)
                {
                    [match.Key] = match.ModRevision,
                }
            );
        }

        return response.HeaderRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ":";
    }

    private IEtcdClient ResolveClient(ConfiglueResourceContext context)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _clientResolver(context.Route);
    }

    private EtcdAddress ResolveAddress(ConfiglueResourceContext context)
    {
        var prefix = _options.KeyPrefixSelector?.Invoke(context) ?? _options.KeyPrefix;
        var normalized = EtcdKeyEncoding.NormalizePrefix(prefix);
        var subjectPart = context.ResourceKey.Value;
        var key = string.IsNullOrEmpty(subjectPart)
            ? normalized + "/value"
            : normalized + "/" + EtcdKeyEncoding.EscapeSegment(subjectPart);
        return new EtcdAddress(key, normalized + "/");
    }

    private static (
        IReadOnlyList<EtcdCompare> Compares,
        IReadOnlyList<EtcdWrite> Writes
    ) PrepareSingleWrite(string key, ResourceWriteRequest request)
    {
        IReadOnlyList<EtcdCompare> compares = BuildCompares(key, request.Condition);
        IReadOnlyList<EtcdWrite> writes = [new EtcdPut(key, request.Content.ToArray())];
        return (compares, writes);
    }

    internal static IReadOnlyList<EtcdCompare> BuildCompares(
        string key,
        RevisionCondition condition
    )
    {
        if (condition.IsNone)
        {
            return [];
        }

        if (condition.IsMustNotExist)
        {
            return [new EtcdCompare(key, EtcdCompareKind.KeyNotExists, 0)];
        }

        if (
            !EtcdRevisionCodec.TryDecode(condition.Revision, out _, out var keyRevisions)
            || !keyRevisions.TryGetValue(key, out var expected)
        )
        {
            throw new StateConflictException(
                $"The etcd key '{key}' no longer matches its expected revision."
            );
        }

        return [new EtcdCompare(key, EtcdCompareKind.ModRevisionEqual, expected)];
    }

    private static StateConflictException CreateConflict(string key) =>
        new($"The etcd key '{key}' changed after it was read.");

    private static bool IsSatisfied(RevisionCondition condition, string key, long? modRevision)
    {
        if (condition.IsNone)
        {
            return true;
        }

        if (condition.IsMustNotExist)
        {
            return modRevision is null;
        }

        return EtcdRevisionCodec.TryDecode(condition.Revision, out _, out var keyRevisions)
            && (
                keyRevisions.TryGetValue(key, out var expected)
                    ? modRevision == expected
                    : modRevision is null
            );
    }

    private static string CreateIdentityHash(string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);
#if NETSTANDARD
        using var hash = SHA256.Create();
        return Convert.ToHexString(hash.ComputeHash(bytes)).ToLowerInvariant();
#else
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
#endif
    }

    private sealed record EtcdAddress(string Key, string WatchPrefix);

    private sealed class CombinedEtcdClient(IEtcdKvClient kv, IEtcdWatcherClient watcher)
        : IEtcdClient
    {
        public IEtcdKvClient Kv { get; } = kv;

        public IEtcdWatcherClient Watcher { get; } = watcher;

        public ValueTask<EtcdRangeResponse> GetPrefixAsync(
            string prefix,
            long? revision = null,
            CancellationToken cancellationToken = default
        ) => Kv.GetPrefixAsync(prefix, revision, cancellationToken);

        public ValueTask<EtcdTxnResponse> TransactAsync(
            IReadOnlyList<EtcdCompare> compares,
            IReadOnlyList<EtcdWrite> writes,
            CancellationToken cancellationToken = default
        ) => Kv.TransactAsync(compares, writes, cancellationToken);

        public Task WatchPrefixAsync(
            string prefix,
            long? startRevision,
            Func<EtcdWatchResponse, CancellationToken, ValueTask<bool>> onResponse,
            CancellationToken cancellationToken = default
        ) => Watcher.WatchPrefixAsync(prefix, startRevision, onResponse, cancellationToken);

        public void Dispose() { }
    }
}
