namespace Configlue.Resource.Etcd;

/// <summary>
/// The physical batch writer for one etcd coordination domain. It combines disjoint
/// subject-level mutations from <see cref="EtcdSource{TFragment}"/> into a single etcd
/// transaction through the normal <see cref="IResourceBatchWriter"/> capability model.
/// </summary>
internal sealed class EtcdPrefixBatchWriter : IResourceBatchWriter, IResourceBatchCompatibility
{
    private readonly Func<RouteKey, IEtcdClient> _clientResolver;
    private readonly EtcdResourceOptions _options;
    private readonly bool _routeAwareIdentity;

    public EtcdPrefixBatchWriter(
        Func<RouteKey, IEtcdClient> clientResolver,
        EtcdResourceOptions options,
        bool routeAwareIdentity = true
    )
    {
        ArgumentNullException.ThrowIfNull(clientResolver);
        ArgumentNullException.ThrowIfNull(options);
        _clientResolver = clientResolver;
        _options = options;
        _routeAwareIdentity = routeAwareIdentity;
    }

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

    public object? GetBatchCompatibilityToken(ConfiglueResourceContext context) =>
        "etcd-batch:"
        + ResolvePrefix(context)
        + "|endpoints:"
        + string.Join(",", _options.Endpoints);

    public ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    ) => WriteBatchAsync([ResourceWriteMutation.Replace(request, context)], cancellationToken);

    public async ValueTask<StateWriteResult> WriteBatchAsync(
        IReadOnlyList<ResourceWriteMutation> mutations,
        CancellationToken cancellationToken = default
    )
    {
        ResourceWriteMutation.ValidateBatch(mutations);
        _ = ResourceWriteMutation.ResolveBatchSchema(mutations);

        var condition = mutations[0].Condition;
        var puts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var bases = new List<string>();
        var route = mutations[0].Context.Route;
        foreach (var mutation in mutations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mutation.Context.Route != route)
            {
                throw new NotSupportedException(
                    "etcd batch mutations span different routes and cannot share one transaction."
                );
            }
        }

        var client =
            _clientResolver(route)
            ?? throw new InvalidOperationException("The etcd client resolver returned null.");
        foreach (var mutation in mutations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = EtcdBatchPayload.Decode(mutation.Apply(ResourceReadResult.NotFound()));
            if (!bases.Contains(payload.AddressBase))
            {
                bases.Add(payload.AddressBase);
            }

            foreach (var put in payload.Puts)
            {
                puts[put.Key] = Convert.FromBase64String(put.Value);
            }
        }

        var backend = new EtcdStateBackend(client, client, _options);
        try
        {
            var address = new BatchAddress(bases[0] + "/");
            Dictionary<string, long>? baseline = null;
            if (condition.IsMatch)
            {
                if (!EtcdRevisionCodec.TryDecode(condition.Revision, out _, out var decoded))
                {
                    throw new StateConflictException(
                        "The etcd batch no longer matches its expected revision."
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
                    compares.Add(CreatePutCompare(put.Key, condition, baseline));
                }

                writes.Add(new EtcdPut(put.Key, put.Value));
            }

            if (baseline is not null)
            {
                foreach (
                    var key in baseline.Keys.Where(key =>
                        !puts.ContainsKey(key) && IsUnderAnyBase(key, bases)
                    )
                )
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    compares.Add(
                        new EtcdCompare(key, EtcdCompareKind.ModRevisionEqual, baseline[key])
                    );
                    writes.Add(new EtcdDelete(key));
                }
            }

            if (writes.Count == 0)
            {
                if (condition.IsMatch)
                {
                    return new StateWriteResult(condition.Revision);
                }

                var current = await backend
                    .GetPrefixAsync(address.RangePrefix, null, cancellationToken)
                    .ConfigureAwait(false);
                return new StateWriteResult(
                    EtcdRevisionCodec.Encode(
                        current.HeaderRevision,
                        new Dictionary<string, long>(StringComparer.Ordinal)
                    )
                );
            }

            var response = await backend
                .TransactAsync(compares, writes, cancellationToken)
                .ConfigureAwait(false);
            if (!response.Succeeded)
            {
                throw new StateConflictException("The etcd batch changed after it was read.");
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
        finally
        {
            backend.Dispose();
        }
    }

    private static EtcdCompare CreatePutCompare(
        string key,
        RevisionCondition condition,
        Dictionary<string, long>? baseline
    )
    {
        if (condition.IsMustNotExist)
        {
            return new EtcdCompare(key, EtcdCompareKind.KeyNotExists, 0);
        }

        if (baseline!.TryGetValue(key, out var expected))
        {
            return new EtcdCompare(key, EtcdCompareKind.ModRevisionEqual, expected);
        }

        return new EtcdCompare(key, EtcdCompareKind.KeyNotExists, 0);
    }

    private static bool IsUnderAnyBase(string key, List<string> bases) =>
        bases.Any(addressBase => key.StartsWith(addressBase + "/", StringComparison.Ordinal));

    private string ResolvePrefix(ConfiglueResourceContext context)
    {
        var prefix = _options.KeyPrefixSelector?.Invoke(context) ?? _options.KeyPrefix;
        return EtcdKeyEncoding.NormalizePrefix(prefix);
    }

    private sealed record BatchAddress(string RangePrefix);
}
