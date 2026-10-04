using System.Text;
using System.Text.Json;
using Configlue.CompilerServices;
using Configlue.Sources;

namespace Configlue.Resource.Etcd;

/// <summary>
/// A typed etcd v3 source mapping a configurable key prefix to a model contribution.
/// Each leaf member has one stable etcd key derived from generated schema metadata;
/// nested members contribute their leaves under the nested path. Reads capture etcd
/// header and per-key modification revisions; the Configlue revision carries both so
/// writes can detect a stale baseline. One fragment write commits all of its keys in
/// a single compare-and-swap transaction, and compare failures become
/// <see cref="StateConflictException"/>.
/// </summary>
public sealed class EtcdSource<TFragment>
    : ISourceWriter<TFragment>,
        ISourceWatcher,
        ISourceCapabilities<TFragment>,
        IAsyncSourceWriteBatchParticipant<TFragment>,
        IResourceIdentity,
        IDisposable
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly Func<RouteKey, IEtcdClient> _clientResolver;
    private readonly EtcdResourceOptions _options;
    private readonly EtcdMemberMapper _mapper;
    private readonly bool _writable;
    private readonly bool _routeAwareIdentity;
    private readonly EtcdPrefixBatchWriter _batchWriter;
    private readonly EtcdWatchShutdown _watchShutdown = new();
    private int _disposed;

    /// <summary>Creates a writable source that uses one shared caller-owned etcd client.</summary>
    /// <param name="schema">The generated model schema backing member mapping.</param>
    /// <param name="client">The caller-owned etcd client used for every route.</param>
    /// <param name="options">Key prefix, endpoint, and transport settings.</param>
    public EtcdSource(
        ConfiglueModelSchema schema,
        IEtcdClient client,
        EtcdResourceOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(client);
        _mapper = new EtcdMemberMapper(schema);
        _options = options ?? new EtcdResourceOptions();
        _options.Validate();
        _clientResolver = _ => client;
        _writable = true;
        _routeAwareIdentity = false;
        _batchWriter = new EtcdPrefixBatchWriter(_clientResolver, _options, _routeAwareIdentity);
    }

    /// <summary>Creates a source that uses one shared caller-owned etcd client.</summary>
    /// <param name="schema">The generated model schema backing member mapping.</param>
    /// <param name="client">The caller-owned etcd client used for every route.</param>
    /// <param name="options">Key prefix, endpoint, and transport settings.</param>
    /// <param name="writable">Whether this source exposes a writer.</param>
    public EtcdSource(
        ConfiglueModelSchema schema,
        IEtcdClient client,
        EtcdResourceOptions? options,
        bool writable
    )
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(client);
        _mapper = new EtcdMemberMapper(schema);
        _options = options ?? new EtcdResourceOptions();
        _options.Validate();
        _clientResolver = _ => client;
        _writable = writable;
        _routeAwareIdentity = false;
        _batchWriter = new EtcdPrefixBatchWriter(_clientResolver, _options, _routeAwareIdentity);
    }

    /// <summary>Creates a source that resolves a shared caller-owned etcd client per route.</summary>
    /// <param name="schema">The generated model schema backing member mapping.</param>
    /// <param name="clientResolver">Resolves the caller-owned etcd client for each route.</param>
    /// <param name="options">Key prefix, endpoint, and transport settings.</param>
    /// <param name="writable">Whether this source exposes a writer.</param>
    public EtcdSource(
        ConfiglueModelSchema schema,
        Func<RouteKey, IEtcdClient> clientResolver,
        EtcdResourceOptions? options = null,
        bool writable = true
    )
        : this(
            schema,
            route =>
                clientResolver(route)
                ?? throw new InvalidOperationException("The etcd client resolver returned null."),
            options,
            true,
            writable
        )
    {
        ArgumentNullException.ThrowIfNull(clientResolver);
    }

    internal EtcdSource(
        ConfiglueModelSchema schema,
        Func<RouteKey, IEtcdKvClient> kvResolver,
        Func<RouteKey, IEtcdWatcherClient> watcherResolver,
        EtcdResourceOptions? options = null,
        bool writable = true
    )
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(kvResolver);
        ArgumentNullException.ThrowIfNull(watcherResolver);
        _mapper = new EtcdMemberMapper(schema);
        _options = options ?? new EtcdResourceOptions();
        _options.Validate();
        _clientResolver = route =>
        {
            var kv =
                kvResolver(route)
                ?? throw new InvalidOperationException("The etcd client resolver returned null.");
            var watcher =
                watcherResolver(route)
                ?? throw new InvalidOperationException("The etcd client resolver returned null.");
            return new EtcdClientPair(kv, watcher);
        };
        _writable = writable;
        _routeAwareIdentity = true;
        _batchWriter = new EtcdPrefixBatchWriter(_clientResolver, _options, _routeAwareIdentity);
    }

    private EtcdSource(
        ConfiglueModelSchema schema,
        Func<RouteKey, IEtcdClient> clientResolver,
        EtcdResourceOptions? options,
        bool routeAwareIdentity,
        bool writable
    )
    {
        ArgumentNullException.ThrowIfNull(schema);
        _mapper = new EtcdMemberMapper(schema);
        _options = options ?? new EtcdResourceOptions();
        _options.Validate();
        _clientResolver = clientResolver;
        _writable = writable;
        _routeAwareIdentity = routeAwareIdentity;
        _batchWriter = new EtcdPrefixBatchWriter(_clientResolver, _options, _routeAwareIdentity);
    }

    /// <summary>The generated model schema backing member mapping.</summary>
    public ConfiglueModelSchema Schema => _mapper.Schema;

    /// <inheritdoc />
    public ISourceWriter<TFragment>? Writer => _writable ? this : null;

    /// <inheritdoc />
    public ISourceWatcher? Watcher => this;

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context)
    {
        if (_options.FixedResourceId is { } explicitId)
        {
            return explicitId;
        }

        var prefix = ResolvePrefix(context);
        return new ResourceId(
            "etcd:"
                + EtcdIdentityHash.Create(
                    prefix,
                    string.Join(",", _options.Endpoints),
                    _routeAwareIdentity ? context.Route.Value : string.Empty
                )
        );
    }

    /// <inheritdoc />
    public async ValueTask<StateReadResult<TFragment>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var backend = AcquireBackend(context);
        try
        {
            var address = ResolveAddress(context);
            var response = await backend
                .GetPrefixAsync(address.RangePrefix, null, cancellationToken)
                .ConfigureAwait(false);
            return ConvertReadResult(response, address);
        }
        finally
        {
            backend.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<TFragment> request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_writable)
        {
            throw new InvalidOperationException("This etcd source is read-only.");
        }

        if (request.Value is not IConfiglueFragment fragment)
        {
            throw new InvalidOperationException("An etcd source write requires a fragment value.");
        }

        var backend = AcquireBackend(context);
        try
        {
            var address = ResolveAddress(context);
            var assignments = _mapper.FlattenFragment(fragment);
            var puts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var assignment in assignments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                puts[address.RangePrefix + assignment.MemberSuffix] =
                    EtcdMemberMapper.SerializeLeaf(
                        assignment.Value,
                        assignment.LeafType,
                        address.RangePrefix + assignment.MemberSuffix
                    );
            }

            return await WriteKeysAsync(
                    backend,
                    address,
                    request.Condition,
                    puts,
                    null,
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
    public ValueTask<StateWriteBatchPlan?> TryCreateBatchWriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<TFragment> request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_writable)
        {
            return new ValueTask<StateWriteBatchPlan?>((StateWriteBatchPlan?)null);
        }

        if (request.Value is not IConfiglueFragment fragment)
        {
            throw new InvalidOperationException("An etcd source write requires a fragment value.");
        }

        var address = ResolveAddress(context);
        var assignments = _mapper.FlattenFragment(fragment);
        var payload = EtcdBatchPayload.FromAssignments(address.AddressBase, assignments);
        var scope =
            "etcd/"
            + ResolvePrefix(context)
            + "/"
            + (address.SubjectPart.Length == 0 ? "-" : address.SubjectPart);
        var mutation = new ResourceWriteMutation(
            request.Condition,
            null,
            _ => payload.Encode(),
            scope,
            canCompose: true,
            context
        );
        return new ValueTask<StateWriteBatchPlan?>(
            new StateWriteBatchPlan(GetResourceId(context), _batchWriter, mutation)
        );
    }

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        var backend = AcquireBackend(context);
        try
        {
            var address = ResolveAddress(context);
            await _watchShutdown
                .WaitAsync(
                    watchCancellationToken =>
                        backend.WaitForChangeAsync(
                            address.RangePrefix,
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

    private StateReadResult<TFragment> ConvertReadResult(
        EtcdRangeResponse response,
        EtcdAddress address
    )
    {
        var stored = new List<EtcdStoredLeaf>();
        var keyRevisions = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var kv in response.Kvs)
        {
            if (!EtcdKeyEncoding.TrySplitMemberSuffix(address.AddressBase, kv.Key, out var suffix))
            {
                continue;
            }

            if (!_mapper.TryGetLeaf(suffix, out _))
            {
                continue;
            }

            stored.Add(new EtcdStoredLeaf(kv.Key, suffix, kv.Value.ToArray()));
            keyRevisions[kv.Key] = kv.ModRevision;
        }

        var revision = EtcdRevisionCodec.Encode(response.HeaderRevision, keyRevisions);
        if (stored.Count == 0)
        {
            return StateReadResult<TFragment>.NotFound(revision);
        }

        IConfiglueFragment fragment;
        try
        {
            fragment = _mapper.BuildFragment(stored);
        }
        catch (FormatException)
        {
            return StateReadResult<TFragment>.InvalidPayload(default, revision);
        }

        return StateReadResult<TFragment>.Success(
            (TFragment)fragment,
            revision,
            _mapper.Schema.ToMetadata()
        );
    }

    private async ValueTask<string?> ReadRevisionAsync(
        EtcdStateBackend backend,
        EtcdAddress address,
        CancellationToken cancellationToken
    )
    {
        var response = await backend
            .GetPrefixAsync(address.RangePrefix, null, cancellationToken)
            .ConfigureAwait(false);
        var keyRevisions = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var kv in response.Kvs)
        {
            if (
                EtcdKeyEncoding.TrySplitMemberSuffix(address.AddressBase, kv.Key, out var suffix)
                && _mapper.TryGetLeaf(suffix, out _)
            )
            {
                keyRevisions[kv.Key] = kv.ModRevision;
            }
        }

        return EtcdRevisionCodec.Encode(response.HeaderRevision, keyRevisions);
    }

    private async ValueTask<StateWriteResult> WriteKeysAsync(
        EtcdStateBackend backend,
        EtcdAddress address,
        RevisionCondition condition,
        IReadOnlyDictionary<string, byte[]> puts,
        IReadOnlyCollection<string>? knownBases,
        CancellationToken cancellationToken
    )
    {
        Dictionary<string, long>? baseline = null;
        if (condition.IsMatch)
        {
            if (!EtcdRevisionCodec.TryDecode(condition.Revision, out _, out var decoded))
            {
                throw new StateConflictException(
                    $"The etcd prefix '{address.RangePrefix}' no longer matches its expected revision."
                );
            }

            baseline = decoded;
        }

        var compares = new List<EtcdCompare>();
        var writes = new List<EtcdWrite>();
        foreach (var put in puts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!condition.IsNone)
            {
                compares.Add(CreateCompare(put.Key, condition, baseline));
            }

            writes.Add(new EtcdPut(put.Key, put.Value));
        }

        if (baseline is not null)
        {
            foreach (var key in baseline.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (puts.ContainsKey(key) || !IsUnderBase(key, address, knownBases))
                {
                    continue;
                }

                compares.Add(new EtcdCompare(key, EtcdCompareKind.ModRevisionEqual, baseline[key]));
                writes.Add(new EtcdDelete(key));
            }
        }

        if (writes.Count == 0)
        {
            var current = await backend
                .GetPrefixAsync(address.RangePrefix, null, cancellationToken)
                .ConfigureAwait(false);
            var keyRevisions = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var kv in current.Kvs)
            {
                if (
                    EtcdKeyEncoding.TrySplitMemberSuffix(
                        address.AddressBase,
                        kv.Key,
                        out var suffix
                    ) && _mapper.TryGetLeaf(suffix, out _)
                )
                {
                    keyRevisions[kv.Key] = kv.ModRevision;
                }
            }

            return new StateWriteResult(
                EtcdRevisionCodec.Encode(current.HeaderRevision, keyRevisions)
            );
        }

        var response = await backend
            .TransactAsync(compares, writes, cancellationToken)
            .ConfigureAwait(false);
        if (!response.Succeeded)
        {
            throw new StateConflictException(
                $"The etcd prefix '{address.RangePrefix}' changed after it was read."
            );
        }

        var next = baseline is null
            ? new Dictionary<string, long>(StringComparer.Ordinal)
            : new Dictionary<string, long>(baseline, StringComparer.Ordinal);
        foreach (var put in puts)
        {
            next[put.Key] = response.HeaderRevision;
        }

        if (baseline is not null)
        {
            foreach (var write in writes)
            {
                if (write is EtcdDelete delete)
                {
                    next.Remove(delete.Key);
                }
            }
        }

        return new StateWriteResult(EtcdRevisionCodec.Encode(response.HeaderRevision, next));
    }

    private static EtcdCompare CreateCompare(
        string key,
        RevisionCondition condition,
        Dictionary<string, long>? baseline
    )
    {
        if (condition.IsNone)
        {
            throw new InvalidOperationException("Unconditional writes carry no compares.");
        }

        if (condition.IsMustNotExist)
        {
            return new EtcdCompare(key, EtcdCompareKind.KeyNotExists, 0);
        }

        return baseline!.TryGetValue(key, out var expected)
            ? new EtcdCompare(key, EtcdCompareKind.ModRevisionEqual, expected)
            : new EtcdCompare(key, EtcdCompareKind.KeyNotExists, 0);
    }

    private static bool IsUnderBase(
        string key,
        EtcdAddress address,
        IReadOnlyCollection<string>? knownBases
    )
    {
        if (knownBases is null)
        {
            return key.StartsWith(address.RangePrefix, StringComparison.Ordinal);
        }

        return knownBases.Any(addressBase => key.StartsWith(addressBase, StringComparison.Ordinal));
    }

    private EtcdStateBackend AcquireBackend(ConfiglueResourceContext context)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var client = _clientResolver(context.Route);
        return new EtcdStateBackend(client, client, _options);
    }

    private string ResolvePrefix(ConfiglueResourceContext context)
    {
        var prefix = _options.KeyPrefixSelector?.Invoke(context) ?? _options.KeyPrefix;
        return EtcdKeyEncoding.NormalizePrefix(prefix);
    }

    private EtcdAddress ResolveAddress(ConfiglueResourceContext context)
    {
        var normalized = ResolvePrefix(context);
        var subjectPart = context.ResourceKey.Value;
        var addressBase = string.IsNullOrEmpty(subjectPart)
            ? normalized
            : normalized + "/" + EtcdKeyEncoding.EscapeSegment(subjectPart);
        return new EtcdAddress(addressBase, addressBase + "/", subjectPart);
    }

    private sealed record EtcdAddress(string AddressBase, string RangePrefix, string SubjectPart);

    private sealed class EtcdClientPair(IEtcdKvClient kv, IEtcdWatcherClient watcher) : IEtcdClient
    {
        public ValueTask<EtcdRangeResponse> GetPrefixAsync(
            string prefix,
            long? revision = null,
            CancellationToken cancellationToken = default
        ) => kv.GetPrefixAsync(prefix, revision, cancellationToken);

        public ValueTask<EtcdTxnResponse> TransactAsync(
            IReadOnlyList<EtcdCompare> compares,
            IReadOnlyList<EtcdWrite> writes,
            CancellationToken cancellationToken = default
        ) => kv.TransactAsync(compares, writes, cancellationToken);

        public Task WatchPrefixAsync(
            string prefix,
            long? startRevision,
            Func<EtcdWatchResponse, CancellationToken, ValueTask<bool>> onResponse,
            CancellationToken cancellationToken = default
        ) => watcher.WatchPrefixAsync(prefix, startRevision, onResponse, cancellationToken);

        public void Dispose() { }
    }
}

internal sealed record EtcdBatchPayload(string AddressBase, Dictionary<string, string> Puts)
{
    public static EtcdBatchPayload FromAssignments(
        string addressBase,
        List<EtcdLeafAssignment> assignments
    )
    {
        var puts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assignment in assignments)
        {
            var key = addressBase + "/" + assignment.MemberSuffix;
            puts[key] = Convert.ToBase64String(
                EtcdMemberMapper.SerializeLeaf(assignment.Value, assignment.LeafType, key)
            );
        }

        return new EtcdBatchPayload(addressBase, puts);
    }

    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this);

    public static EtcdBatchPayload Decode(ReadOnlyMemory<byte> content) =>
        JsonSerializer.Deserialize<EtcdBatchPayload>(content.Span)
        ?? throw new FormatException("An etcd batch mutation carries no payload.");
}

internal static class EtcdIdentityHash
{
    public static string Create(params string[] values)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256
        );
        var lengthBuffer = new byte[sizeof(int)];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            lengthBuffer[0] = (byte)(bytes.Length >> 24);
            lengthBuffer[1] = (byte)(bytes.Length >> 16);
            lengthBuffer[2] = (byte)(bytes.Length >> 8);
            lengthBuffer[3] = (byte)bytes.Length;
            hash.AppendData(lengthBuffer, 0, lengthBuffer.Length);
            hash.AppendData(bytes, 0, bytes.Length);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
