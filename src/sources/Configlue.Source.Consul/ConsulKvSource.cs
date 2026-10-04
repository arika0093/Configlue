using System.Globalization;
using System.Text;
using System.Text.Json;
using Configlue.CompilerServices;
using Configlue.Internal;

namespace Configlue.Source.Consul;

/// <summary>
/// A Consul KV prefix source that maps keys under one prefix to one model contribution.
/// </summary>
/// <remarks>
/// <para>Reads use recursive KV reads; the Consul index is exposed as the revision.</para>
/// <para>Writes are opt-in and use CAS semantics; multi-key updates use the atomic transaction API.</para>
/// <para>Provenance exposes only the key prefix, datacenter, namespace, and modify indexes.</para>
/// </remarks>
public sealed class ConsulKvSource<TFragment>
    : ISourceCapabilities<TFragment>,
        ISourceWriter<TFragment>,
        ISourceWatcher,
        IResourceIdentity,
        IAsyncSourceWriteBatchParticipant<TFragment>,
        IResourceBatchCompatibility,
        IDisposable
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly IConsulKvClient _client;
    private readonly string _prefix;
    private readonly ConfiglueModelSchema _schema;
    private readonly ConsulKvPrefixOptions _options;
    private readonly Func<RouteKey, IConsulKvClient>? _clientResolver;
    private readonly bool _writable;
    private readonly Dictionary<ConfiglueModelSchema, ConsulMemberLookup> _lookups;
    private readonly ConsulKvBatchWriter<TFragment> _batchWriter;
    private readonly WatchShutdown _watchShutdown = new();
    private int _disposed;

    /// <summary>Creates a prefix source for the supplied schema.</summary>
    public ConsulKvSource(
        IConsulKvClient client,
        ConfiglueModelSchema schema,
        string prefix,
        ConsulKvPrefixOptions? options = null,
        bool writable = true
    )
        : this(_ => client, schema, prefix, options, writable)
    {
        ArgumentNullException.ThrowIfNull(client);
    }

    /// <summary>Creates a prefix source that resolves a shared client for each physical route.</summary>
    public ConsulKvSource(
        Func<RouteKey, IConsulKvClient> clientResolver,
        ConfiglueModelSchema schema,
        string prefix,
        ConsulKvPrefixOptions? options = null,
        bool writable = true
    )
    {
        ArgumentNullException.ThrowIfNull(clientResolver);
        ArgumentNullException.ThrowIfNull(schema);
        _prefix = ConsulKeyNormalization.NormalizePrefix(prefix);
        _schema = schema;
        _options = options ?? new ConsulKvPrefixOptions();
        _options.Validate();
        ValidateTimeout(_options.BlockingWaitTimeout);
        _clientResolver = clientResolver;
        _client = new RouteClient(clientResolver);
        _writable = writable;
        _lookups = BuildLookups(schema);
        _batchWriter = new ConsulKvBatchWriter<TFragment>(this);
    }

    internal IConsulKvClient Client => _client;

    internal string Prefix => _prefix;

    internal ConsulKvPrefixOptions PrefixOptions => _options;

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

        var datacenter = ResolveDatacenter(context) ?? string.Empty;
        var ns = ResolveNamespace(context) ?? string.Empty;
        var partition = ResolvePartition(context) ?? string.Empty;
        return new ResourceId(
            "consul:"
                + ConsulIdentityHash.Create(
                    datacenter,
                    ns,
                    partition,
                    context.ModelId ?? string.Empty
                )
        );
    }

    /// <inheritdoc />
    public object? GetBatchCompatibilityToken(ConfiglueResourceContext context)
    {
        return string.Join(
            "\n",
            ResolveDatacenter(context) ?? string.Empty,
            ResolveNamespace(context) ?? string.Empty,
            ResolvePartition(context) ?? string.Empty
        );
    }

    /// <inheritdoc />
    public async ValueTask<StateReadResult<TFragment>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var prefix = ResolvePrefix(context);
        var result = await ResolveClient(context.Route)
            .ListAsync(prefix, CreateListOptions(context, waitIndex: null), cancellationToken)
            .ConfigureAwait(false);
        var revision = ConsulKeyNormalization.FormatRevision(result.ConsulIndex);
        if (result.Entries.Count == 0)
        {
            return StateReadResult<TFragment>.NotFound(revision);
        }

        var assignments = MapEntries(prefix, result.Entries);
        if (assignments.Count == 0)
        {
            return StateReadResult<TFragment>.NotFound(revision);
        }

        IConfiglueFragment fragment = _schema.CreateEmptyFragment();
        var matchedAny = false;
        foreach (var assignment in assignments.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object? decoded;
            try
            {
                decoded = ConsulValueConverter.Decode(
                    assignment.Value,
                    assignment.Member.ValueType,
                    assignment.ConsulKey
                );
            }
            catch (FormatException)
            {
                return StateReadResult<TFragment>.InvalidPayload(default, revision);
            }

            var applied = SetValue(fragment, _schema, assignment.Path, 0, decoded);
            fragment = applied.Fragment;
            matchedAny |= applied.Matched;
        }

        if (!matchedAny)
        {
            return StateReadResult<TFragment>.NotFound(revision);
        }

        return StateReadResult<TFragment>.Success(
            (TFragment)fragment,
            revision,
            _schema.ToMetadata()
        );
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<TFragment> request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_writable)
        {
            throw new InvalidOperationException($"The Consul source '{_prefix}' is read-only.");
        }

        var plan = await BuildWritePlanAsync(context, request, cancellationToken)
            .ConfigureAwait(false);
        return await _batchWriter.WriteSingleAsync(plan, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteBatchPlan?> TryCreateBatchWriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<TFragment> request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_writable || !_options.UseTransaction)
        {
            return null;
        }

        var plan = await BuildWritePlanAsync(context, request, cancellationToken)
            .ConfigureAwait(false);
        var payload = _batchWriter.EncodePayload(plan);
        var mutation = new ResourceWriteMutation(
            default,
            request.Value is IConfiglueFragment fragment ? fragment.Schema.ToMetadata() : null,
            _ => payload,
            scope: "consul/" + plan.EffectivePrefix.Replace("/", "."),
            canCompose: true,
            context
        );
        return new StateWriteBatchPlan(GetResourceId(context), _batchWriter, mutation);
    }

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        var prefix = ResolvePrefix(context);
        var client = ResolveClient(context.Route);
        var waitIndex = ConsulKeyNormalization.TryParseRevision(observedRevision, out var parsed)
            ? parsed
            : (ulong?)null;
        var listOptions = CreateListOptions(context, waitIndex);
        await _watchShutdown
            .WaitAsync(
                watchCancellationToken =>
                    WaitCoreAsync(client, prefix, listOptions, watchCancellationToken),
                cancellationToken
            )
            .ConfigureAwait(false);
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

    /// <summary>Returns a redacted description that never contains credentials.</summary>
    public override string ToString() => $"consul:{_prefix}";

    internal string ResolvePrefix(ConfiglueResourceContext context) =>
        ConsulKeyNormalization.NormalizePrefix(
            _options.KeyPrefixSelector?.Invoke(context) ?? _prefix
        );

    internal string? ResolveDatacenter(ConfiglueResourceContext context) =>
        _options.DatacenterSelector?.Invoke(context) ?? _options.Datacenter;

    internal string? ResolveNamespace(ConfiglueResourceContext context) =>
        _options.NamespaceSelector?.Invoke(context) ?? _options.Namespace;

    internal string? ResolvePartition(ConfiglueResourceContext context) =>
        _options.PartitionSelector?.Invoke(context) ?? _options.Partition;

    internal IConsulKvClient ResolveClient(RouteKey route) =>
        _clientResolver?.Invoke(route)
        ?? (
            _client is RouteClient
                ? throw new InvalidOperationException("No client resolver.")
                : _client
        );

    private ConsulKvListOptions CreateListOptions(
        ConfiglueResourceContext context,
        ulong? waitIndex
    ) =>
        new()
        {
            Datacenter = ResolveDatacenter(context),
            Namespace = ResolveNamespace(context),
            Partition = ResolvePartition(context),
            Consistency = _options.Consistency,
            WaitIndex = waitIndex,
            WaitTimeout = waitIndex is null ? null : _options.BlockingWaitTimeout,
        };

    private ConsulKvWriteOptions CreateWriteOptions(ConfiglueResourceContext context) =>
        new()
        {
            Datacenter = ResolveDatacenter(context),
            Namespace = ResolveNamespace(context),
            Partition = ResolvePartition(context),
        };

    private static async ValueTask WaitCoreAsync(
        IConsulKvClient client,
        string prefix,
        ConsulKvListOptions listOptions,
        CancellationToken cancellationToken
    )
    {
        var observed = listOptions.WaitIndex;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConsulKvListResult result;
            try
            {
                result = await client
                    .ListAsync(prefix, listOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
                when (exception is HttpRequestException or TimeoutException or IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (observed is null || result.ConsulIndex != observed)
            {
                return;
            }

            return;
        }
    }

    private Dictionary<string, ConsulAssignment> MapEntries(
        string prefix,
        IReadOnlyList<ConsulKvEntry> entries
    )
    {
        var assignments = new Dictionary<string, ConsulAssignment>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var entry in entries)
        {
            if (
                !entry.Key.Equals(prefix, StringComparison.Ordinal)
                && !entry.Key.StartsWith(prefix + "/", StringComparison.Ordinal)
            )
            {
                continue;
            }

            if (entry.Key.Length == prefix.Length)
            {
                throw new FormatException(
                    $"The Consul key '{entry.Key}' names the prefix itself. Use child keys to set members."
                );
            }

            var relative = entry.Key[(prefix.Length + 1)..];
            if (relative.Length == 0)
            {
                continue;
            }

            var segments = relative.Split('/');
            if (segments.Any(string.IsNullOrWhiteSpace))
            {
                throw new FormatException(
                    $"The Consul key '{entry.Key}' contains an empty path segment."
                );
            }

            var canonical = ResolveCanonicalPath(_schema, segments, entry.Key);
            if (canonical is null)
            {
                continue;
            }

            var target = string.Join(".", canonical.Path);
            var value = entry.Value is null ? ReadOnlyMemory<byte>.Empty : entry.Value;
            var assignment = new ConsulAssignment(
                canonical.Path,
                canonical.Member,
                value,
                entry.Key,
                entry.ModifyIndex
            );
            if (!assignments.TryAdd(target, assignment))
            {
                throw new InvalidOperationException(
                    $"More than one Consul key maps to model property '{target}'."
                );
            }
        }

        return assignments;
    }

    private CanonicalMember ResolveCanonicalPath(
        ConfiglueModelSchema schema,
        string[] segments,
        string consulKey
    )
    {
        var canonicalPath = new string[segments.Length];
        var current = schema;
        ConfiglueMemberSchema leaf = default;
        for (var index = 0; index < segments.Length; index++)
        {
            if (!_lookups[current].TryResolve(segments[index], out var member, out var ambiguous))
            {
                if (ambiguous)
                {
                    throw new FormatException(
                        $"The Consul path segment '{segments[index]}' is ambiguous in schema '{current.Id}'."
                    );
                }

                return null!;
            }

            canonicalPath[index] = member.Name;
            leaf = member;
            if (index == segments.Length - 1)
            {
                break;
            }

            if (member.NestedSchemaFactory is null)
            {
                throw new FormatException(
                    $"The Consul key '{consulKey}' continues past non-nested member '{member.Name}'."
                );
            }

            current = member.NestedSchemaFactory();
        }

        if (leaf.NestedSchemaFactory is not null)
        {
            throw new FormatException(
                $"The Consul key '{consulKey}' names a nested model. Use additional '/' segments to set its members."
            );
        }

        return new CanonicalMember(canonicalPath, leaf);
    }

    private AppliedFragment SetValue(
        IConfiglueFragment fragment,
        ConfiglueModelSchema schema,
        string[] path,
        int pathIndex,
        object? value
    )
    {
        if (!_lookups[schema].TryResolve(path[pathIndex], out var member, out var ambiguous))
        {
            if (ambiguous)
            {
                throw new FormatException(
                    $"The Consul path segment '{path[pathIndex]}' is ambiguous in schema '{schema.Id}'."
                );
            }

            return new AppliedFragment(fragment, false);
        }

        if (pathIndex == path.Length - 1)
        {
            if (member.NestedSchemaFactory is not null)
            {
                throw new FormatException(
                    $"The Consul key names a nested model '{member.Name}'. Use additional segments."
                );
            }

            ValidateValueType(value, member);
            return new AppliedFragment(fragment.WithMember(member.Id, value), true);
        }

        if (member.NestedSchemaFactory is null)
        {
            throw new FormatException(
                $"The Consul key continues past non-nested member '{member.Name}'."
            );
        }

        var nestedSchema = member.NestedSchemaFactory();
        var nestedFragment =
            FindPresentMember(fragment, member.Id) as IConfiglueFragment
            ?? nestedSchema.CreateEmptyFragment();
        var nested = SetValue(nestedFragment, nestedSchema, path, pathIndex + 1, value);
        return nested.Matched
            ? new AppliedFragment(fragment.WithMember(member.Id, nested.Fragment), true)
            : new AppliedFragment(fragment, false);
    }

    private static void ValidateValueType(object? value, ConfiglueMemberSchema member)
    {
        if (value is null)
        {
            if (
                member.ValueType.IsValueType && Nullable.GetUnderlyingType(member.ValueType) is null
            )
            {
                throw new FormatException(
                    $"The Consul value for non-nullable member '{member.Name}' cannot be null."
                );
            }

            return;
        }

        var valueType = Nullable.GetUnderlyingType(member.ValueType) ?? member.ValueType;
        if (!valueType.IsInstanceOfType(value))
        {
            throw new FormatException(
                $"The Consul value for member '{member.Name}' requires '{member.ValueType}'."
            );
        }
    }

    private static object? FindPresentMember(IConfiglueFragment fragment, int memberId)
    {
        foreach (var member in fragment.EnumeratePresentMembers())
        {
            if (member.Id == memberId)
            {
                return member.Value;
            }
        }

        return null;
    }

    private static Dictionary<ConfiglueModelSchema, ConsulMemberLookup> BuildLookups(
        ConfiglueModelSchema schema
    )
    {
        var lookups = new Dictionary<ConfiglueModelSchema, ConsulMemberLookup>();
        var ancestors = new HashSet<Type>();
        Collect(schema, lookups, ancestors);
        return lookups;

        static void Collect(
            ConfiglueModelSchema current,
            Dictionary<ConfiglueModelSchema, ConsulMemberLookup> lookups,
            HashSet<Type> ancestors
        )
        {
            if (!ancestors.Add(current.ModelType))
            {
                return;
            }

            lookups[current] = ConsulMemberLookup.Create(current);
            for (var index = 0; index < current.Members.Count; index++)
            {
                var member = current.Members[index];
                if (member.NestedSchemaFactory is not null)
                {
                    Collect(member.NestedSchemaFactory(), lookups, ancestors);
                }
            }

            ancestors.Remove(current.ModelType);
        }
    }

    private async ValueTask<ConsulWritePlan> BuildWritePlanAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<TFragment> request,
        CancellationToken cancellationToken
    )
    {
        var prefix = ResolvePrefix(context);
        var client = ResolveClient(context.Route);
        var current = await client
            .ListAsync(prefix, CreateListOptions(context, waitIndex: null), cancellationToken)
            .ConfigureAwait(false);
        if (!request.Condition.IsNone)
        {
            var expectedRevision = request.Condition.IsMustNotExist
                ? null
                : request.Condition.Revision;
            if (expectedRevision is null)
            {
                if (current.Entries.Count != 0)
                {
                    throw new StateConflictException(
                        $"The Consul prefix '{prefix}' already exists."
                    );
                }
            }
            else
            {
                if (!ConsulKeyNormalization.TryParseRevision(expectedRevision, out var expected))
                {
                    throw new StateConflictException(
                        $"The Consul prefix '{prefix}' no longer matches its expected revision."
                    );
                }

                if (current.ConsulIndex != expected)
                {
                    throw new StateConflictException(
                        $"The Consul prefix '{prefix}' changed after it was read."
                    );
                }
            }
        }

        var desired = CollectDesiredKeys(prefix, (IConfiglueFragment)(object)request.Value);
        var currentByKey = new Dictionary<string, ConsulKvEntry>(StringComparer.Ordinal);
        for (var index = 0; index < current.Entries.Count; index++)
        {
            var entry = current.Entries[index];
            if (!currentByKey.TryAdd(entry.Key, entry))
            {
                throw new InvalidOperationException(
                    $"The Consul key '{entry.Key}' is listed more than once."
                );
            }
        }

        var operations = new List<ConsulTxnOperation>();
        foreach (var pair in desired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (currentByKey.TryGetValue(pair.Key, out var existing))
            {
                if (request.Condition.IsMustNotExist)
                {
                    throw new StateConflictException(
                        $"The Consul prefix '{prefix}' already exists."
                    );
                }

                ulong? cas = request.Condition.IsNone ? null : existing.ModifyIndex;
                if (cas is null && _options.UseTransaction)
                {
                    cas = existing.ModifyIndex;
                }

                operations.Add(ConsulTxnOperation.Set(pair.Key, pair.Value, cas));
                currentByKey.Remove(pair.Key);
            }
            else
            {
                if (request.Condition.IsMatch)
                {
                    // The prefix index matched but a desired key is new; another writer may have
                    // deleted it concurrently. Require absence explicitly in transactions.
                    operations.Add(
                        ConsulTxnOperation.Set(
                            pair.Key,
                            pair.Value,
                            _options.UseTransaction ? 0 : null
                        )
                    );
                }
                else if (request.Condition.IsMustNotExist)
                {
                    operations.Add(
                        ConsulTxnOperation.Set(
                            pair.Key,
                            pair.Value,
                            _options.UseTransaction ? (ulong?)0 : null
                        )
                    );
                }
                else
                {
                    operations.Add(ConsulTxnOperation.Set(pair.Key, pair.Value, null));
                }
            }
        }

        foreach (var removed in currentByKey.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                !removed.Key.Equals(prefix, StringComparison.Ordinal)
                && removed.Key.StartsWith(prefix + "/", StringComparison.Ordinal)
            )
            {
                var segments = removed.Key[(prefix.Length + 1)..].Split('/');
                if (ResolveCanonicalPathOrNull(_schema, segments) is not null)
                {
                    ulong? cas = request.Condition.IsNone ? null : removed.ModifyIndex;
                    operations.Add(ConsulTxnOperation.Delete(removed.Key, cas));
                }
            }
        }

        return new ConsulWritePlan(
            context,
            prefix,
            CreateWriteOptions(context),
            operations,
            request.Condition,
            _options.UseTransaction
        );
    }

    private Dictionary<string, byte[]> CollectDesiredKeys(
        string prefix,
        IConfiglueFragment fragment
    )
    {
        var desired = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        CollectFragmentKeys(_schema, fragment, [], prefix, desired);
        return desired;
    }

    private void CollectFragmentKeys(
        ConfiglueModelSchema schema,
        IConfiglueFragment fragment,
        List<string> parentSegments,
        string prefix,
        Dictionary<string, byte[]> desired
    )
    {
        var present = new Dictionary<int, object?>();
        foreach (var member in fragment.EnumeratePresentMembers())
        {
            present[member.Id] = member.Value;
        }

        foreach (var member in schema.Members)
        {
            if (!present.TryGetValue(member.Id, out var value))
            {
                continue;
            }

            if (member.NestedSchemaFactory is not null)
            {
                if (value is null)
                {
                    continue;
                }

                if (value is not IConfiglueFragment nested)
                {
                    throw new FormatException(
                        $"The Consul member '{member.Name}' is not a fragment."
                    );
                }

                parentSegments.Add(member.Name);
                CollectFragmentKeys(
                    member.NestedSchemaFactory(),
                    nested,
                    parentSegments,
                    prefix,
                    desired
                );
                parentSegments.RemoveAt(parentSegments.Count - 1);
                continue;
            }

            var key = prefix + "/" + string.Join("/", parentSegments.Append(member.Name));
            var encoded = ConsulValueConverter.Encode(value, member.ValueType);
            if (!desired.TryAdd(key, encoded))
            {
                throw new InvalidOperationException(
                    $"More than one value maps to Consul key '{key}'."
                );
            }
        }
    }

    private string[]? ResolveCanonicalPathOrNull(ConfiglueModelSchema schema, string[] segments)
    {
        try
        {
            var canonical = ResolveCanonicalPath(schema, segments, string.Join("/", segments));
            return canonical?.Path;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "A blocking wait timeout must be positive."
            );
        }
    }

    private sealed class RouteClient : IConsulKvClient
    {
        private readonly Func<RouteKey, IConsulKvClient> _resolver;

        public RouteClient(Func<RouteKey, IConsulKvClient> resolver) => _resolver = resolver;

        public Task<ConsulKvListResult> ListAsync(
            string prefix,
            ConsulKvListOptions? options,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Resolve a route-specific client before use.");

        public Task<ConsulKvListResult> GetAsync(
            string key,
            ConsulKvListOptions? options,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Resolve a route-specific client before use.");

        public Task<(bool Applied, ulong NewIndex)> PutAsync(
            string key,
            ReadOnlyMemory<byte> value,
            ulong? cas,
            ConsulKvWriteOptions? options,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Resolve a route-specific client before use.");

        public Task<(bool Applied, ulong NewIndex)> DeleteAsync(
            string key,
            ulong? cas,
            ConsulKvWriteOptions? options,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Resolve a route-specific client before use.");

        public Task<ConsulTxnResult> TransactAsync(
            IReadOnlyList<ConsulTxnOperation> operations,
            ConsulKvWriteOptions? options,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("Resolve a route-specific client before use.");
    }

    private sealed record ConsulAssignment(
        string[] Path,
        ConfiglueMemberSchema Member,
        ReadOnlyMemory<byte> Value,
        string ConsulKey,
        ulong ModifyIndex
    );

    private sealed record CanonicalMember(string[] Path, ConfiglueMemberSchema Member);

    private readonly record struct AppliedFragment(IConfiglueFragment Fragment, bool Matched);

    internal sealed record ConsulWritePlan(
        ConfiglueResourceContext Context,
        string EffectivePrefix,
        ConsulKvWriteOptions WriteOptions,
        IReadOnlyList<ConsulTxnOperation> Operations,
        RevisionCondition Condition,
        bool UseTransaction
    );
}
