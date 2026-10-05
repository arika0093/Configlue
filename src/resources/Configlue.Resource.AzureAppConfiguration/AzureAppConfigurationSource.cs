using System.Security.Cryptography;
using System.Text;
using Azure.Core;
using Azure.Data.AppConfiguration;
using Configlue;
using Configlue.CompilerServices;
using Configlue.Internal;
using Configlue.Sources;

namespace Configlue.Resource.AzureAppConfiguration;

/// <summary>
/// A typed Azure App Configuration source that maps selected keys to fragment members.
/// </summary>
/// <remarks>
/// <para>
/// Reads use the Azure SDK directly and preserve ETag, last-modified, and content-type metadata
/// for provenance and concurrency. Feature flags are excluded; Key Vault references are preserved
/// as opaque values.
/// </para>
/// <para>
/// Refresh is pull-based with a configurable minimum interval. The watcher supports monitoring
/// selected keys and a sentinel-key mode; a notification invalidates and re-reads rather than
/// pushing a merged value.
/// </para>
/// <para>Writes are opt-in with ETag conditionals; multi-key commits are non-transactional.</para>
/// </remarks>
/// <typeparam name="TFragment">The generated fragment type.</typeparam>
public sealed class AzureAppConfigurationSource<TFragment>
    : ISourceWriter<TFragment>,
        ISourceWatcher,
        ISourceCapabilities<TFragment>,
        IResourceIdentity,
        IDisposable
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly IAppConfigurationClient _client;
    private readonly ConfiglueModelSchema _schema;
    private readonly AzureAppConfigurationSourceOptions _options;
    private readonly IReadOnlyDictionary<ConfiglueModelSchema, SchemaMemberLookup> _lookups;
    private readonly string _identityHash;
    private readonly WatchShutdown _watchShutdown = new();
    private int _disposed;

    /// <summary>Creates a source from an injected, caller-owned client.</summary>
    public AzureAppConfigurationSource(
        ConfigurationClient client,
        ConfiglueModelSchema schema,
        AzureAppConfigurationSourceOptions? options = null
    )
        : this(
            new AzureAppConfigurationClientAdapter(client),
            schema,
            options ?? new AzureAppConfigurationSourceOptions { Client = client }
        )
    {
        if (options is { Endpoint: not null } or { ConnectionString: not null })
        {
            throw new ArgumentException(
                "Endpoint and ConnectionString cannot be combined with a directly supplied client.",
                nameof(options)
            );
        }
    }

    /// <summary>Creates a source from an endpoint and Azure identity credential.</summary>
    public AzureAppConfigurationSource(
        Uri endpoint,
        TokenCredential credential,
        ConfiglueModelSchema schema,
        AzureAppConfigurationSourceOptions? options = null
    )
        : this(
            new AzureAppConfigurationClientAdapter(new ConfigurationClient(endpoint, credential)),
            schema,
            options ?? new AzureAppConfigurationSourceOptions()
        )
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);
    }

    internal AzureAppConfigurationSource(
        IAppConfigurationClient client,
        ConfiglueModelSchema schema,
        AzureAppConfigurationSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _client = client;
        _schema = schema;
        _options = options;
        _lookups = BuildLookups(schema);
        _identityHash = CreateIdentityHash(options);
    }

    /// <inheritdoc />
    public ISourceWriter<TFragment>? Writer =>
        _options.Writable && _options.SnapshotName is null ? this : null;

    /// <inheritdoc />
    public ISourceWatcher? Watcher => this;

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context)
    {
        _ = context;
        return _options.FixedResourceId ?? new ResourceId($"appconfig:{_identityHash}");
    }

    /// <inheritdoc />
    public async ValueTask<StateReadResult<TFragment>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        _ = context;
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var entries = await _client
            .GetSettingsAsync(_options.ToSelection(), cancellationToken)
            .ConfigureAwait(false);
        return MapToFragment(entries, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<TFragment> request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = context;
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_options.Writable || _options.SnapshotName is not null)
        {
            throw new InvalidOperationException(
                _options.SnapshotName is not null
                    ? $"The App Configuration snapshot '{_options.SnapshotName}' is read-only."
                    : "This App Configuration source is read-only. Set Writable to opt in to writes."
            );
        }

        cancellationToken.ThrowIfCancellationRequested();
        var planned = PlanWrites(request.Value);
        var current = await _client
            .GetSettingsAsync(_options.ToSelection(), cancellationToken)
            .ConfigureAwait(false);
        var currentByKey = IndexByKey(KeepSelectable(current));
        var currentRevision = CreateRevision(KeepSelectable(current));

        if (!request.Condition.IsSatisfiedBy(currentRevision, currentByKey.Count != 0))
        {
            throw new StateConflictException(
                "The App Configuration selection changed after it was read."
            );
        }

        foreach (var plannedWrite in planned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppConfigurationWriteCondition condition;
            string? contentType = null;
            if (
                currentByKey.TryGetValue(
                    (plannedWrite.Key, NormalizeLabel(plannedWrite.Label)),
                    out var existing
                )
            )
            {
                contentType = existing.ContentType;
                condition = request.Condition.IsNone
                    ? AppConfigurationWriteCondition.None
                    : AppConfigurationWriteCondition.Match(
                        existing.ETag
                            ?? throw new StateConflictException(
                                $"The App Configuration setting '{plannedWrite.Key}' has no ETag."
                            )
                    );
            }
            else
            {
                condition = request.Condition.IsNone
                    ? AppConfigurationWriteCondition.None
                    : AppConfigurationWriteCondition.MustNotExist;
            }

            await _client
                .SetSettingAsync(
                    plannedWrite.Key,
                    plannedWrite.Value,
                    plannedWrite.Label,
                    contentType,
                    condition,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        var refreshed = await _client
            .GetSettingsAsync(_options.ToSelection(), cancellationToken)
            .ConfigureAwait(false);
        return new StateWriteResult(CreateRevision(KeepSelectable(refreshed)));
    }

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        _ = context;
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _watchShutdown
            .WaitAsync(
                watchCancellationToken =>
                    PollSelectedOrSentinelAsync(
                        observedRevision,
                        NormalizeRefreshInterval(_options.RefreshInterval),
                        watchCancellationToken
                    ),
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

    /// <summary>
    /// Pull-based refresh shared with other generic polling watches. Sentinel mode
    /// compares the sentinel revision captured at watch start; otherwise the
    /// selection revision is compared against the caller's observed revision.
    /// </summary>
    private async ValueTask PollSelectedOrSentinelAsync(
        string? observedRevision,
        TimeSpan pollInterval,
        CancellationToken cancellationToken
    )
    {
        if (_options.SentinelKey is { } sentinelKey)
        {
            var baseline = await _client
                .GetSettingAsync(sentinelKey, _options.SentinelLabel, cancellationToken)
                .ConfigureAwait(false);
            await PollingWatch
                .WaitForRevisionChangeAsync(
                    cancellation => new ValueTask<string?>(
                        ReadSentinelRevisionAsync(sentinelKey, cancellation)
                    ),
                    SentinelRevision(baseline),
                    pollInterval,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }

        await PollingWatch
            .WaitForRevisionChangeAsync(
                cancellation => new ValueTask<string?>(ReadSelectionRevisionAsync(cancellation)),
                observedRevision,
                pollInterval,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task<string?> ReadSentinelRevisionAsync(
        string sentinelKey,
        CancellationToken cancellationToken
    )
    {
        var current = await _client
            .GetSettingAsync(sentinelKey, _options.SentinelLabel, cancellationToken)
            .ConfigureAwait(false);
        return SentinelRevision(current);
    }

    private async Task<string?> ReadSelectionRevisionAsync(CancellationToken cancellationToken)
    {
        var current = await _client
            .GetSettingsAsync(_options.ToSelection(), cancellationToken)
            .ConfigureAwait(false);
        return CreateRevision(KeepSelectable(current));
    }

    private static TimeSpan NormalizeRefreshInterval(TimeSpan refreshInterval) =>
        refreshInterval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : refreshInterval;

    private StateReadResult<TFragment> MapToFragment(
        IReadOnlyList<AppConfigurationEntry> entries,
        CancellationToken cancellationToken
    )
    {
        var selectable = KeepSelectable(entries);
        var revision = CreateRevision(selectable);
        var assignments = new Dictionary<string, Assignment>(StringComparer.Ordinal);
        var orderedKeys = new List<string>();
        foreach (var entry in selectable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                !AzureAppConfigurationKeyMapper.TryMapToMemberPath(
                    entry.Key,
                    _options.TrimKeyPrefix,
                    out var path
                )
            )
            {
                continue;
            }

            var canonical = ResolveCanonicalPath(_schema, path, entry.Key);
            if (canonical is null)
            {
                continue;
            }

            var target = string.Join(".", canonical);
            if (
                !assignments.TryAdd(
                    target,
                    new Assignment(canonical, entry.Value ?? string.Empty, entry.Key)
                )
            )
            {
                throw new InvalidOperationException(
                    $"More than one App Configuration key maps to model property '{target}'."
                );
            }

            orderedKeys.Add(target);
        }

        if (assignments.Count == 0)
        {
            return StateReadResult<TFragment>.NotFound(revision);
        }

        orderedKeys.Sort(StringComparer.Ordinal);
        IConfiglueFragment fragment = _schema.CreateEmptyFragment();
        var matchedAny = false;
        foreach (var key in orderedKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var assignment = assignments[key];
            var applied = SetValue(
                fragment,
                _schema,
                assignment.Path,
                0,
                assignment.Value,
                assignment.ConfigurationKey,
                key,
                cancellationToken
            );
            fragment = applied.Fragment;
            matchedAny |= applied.Matched;
        }

        return matchedAny
            ? StateReadResult<TFragment>.Success(
                (TFragment)fragment,
                revision,
                _schema.ToMetadata()
            )
            : StateReadResult<TFragment>.NotFound(revision);
    }

    private List<PlannedWrite> PlanWrites(TFragment fragment)
    {
        var writes = new List<PlannedWrite>();
        CollectWrites(fragment, _schema, [], writes);
        writes.Sort(
            static (left, right) => string.Compare(left.Key, right.Key, StringComparison.Ordinal)
        );
        return writes;
    }

    private void CollectWrites(
        IConfiglueFragment fragment,
        ConfiglueModelSchema schema,
        string[] parentPath,
        List<PlannedWrite> writes
    )
    {
        var lookup = _lookups[schema];
        foreach (var member in fragment.EnumeratePresentMembers())
        {
            var memberSchema = schema.Members.FirstOrDefault(candidate =>
                candidate.Id == member.Id
            );
            if (memberSchema.Equals(default(ConfiglueMemberSchema)))
            {
                continue;
            }

            var path = parentPath.Append(memberSchema.Name).ToArray();
            if (memberSchema.NestedSchemaFactory is not null)
            {
                if (member.Value is null)
                {
                    continue;
                }

                if (member.Value is IConfiglueFragment nested)
                {
                    CollectWrites(nested, memberSchema.NestedSchemaFactory(), path, writes);
                    continue;
                }

                throw new FormatException(
                    $"Model property '{string.Join(".", path)}' names a nested model but holds a non-fragment value."
                );
            }

            var configurationKey = AzureAppConfigurationKeyMapper.ToConfigurationKey(
                path,
                _options.TrimKeyPrefix
            );
            if (AzureAppConfigurationKeyMapper.IsFeatureFlagKey(configurationKey))
            {
                throw new InvalidOperationException(
                    $"Model property '{string.Join(".", path)}' maps to reserved feature-flag key '{configurationKey}'."
                );
            }

            var rendered = AzureAppConfigurationValueConverter.Render(
                member.Value,
                memberSchema.ValueType,
                _options.JsonSerializerOptions
            );
            writes.Add(new PlannedWrite(configurationKey, _options.LabelFilter, rendered));
        }

        _ = lookup;
    }

    private AppliedFragment SetValue(
        IConfiglueFragment fragment,
        ConfiglueModelSchema schema,
        string[] path,
        int pathIndex,
        string value,
        string configurationKey,
        string memberPath,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_lookups[schema].TryResolve(path[pathIndex], out var member, out var isAmbiguous))
        {
            if (isAmbiguous)
            {
                throw new FormatException(
                    $"App Configuration path segment '{path[pathIndex]}' is ambiguous in schema '{schema.Id}'."
                );
            }

            return new AppliedFragment(fragment, false);
        }

        if (pathIndex == path.Length - 1)
        {
            if (member.NestedSchemaFactory is not null)
            {
                throw new FormatException(
                    $"App Configuration key '{configurationKey}' names a nested model. Use additional segments to set its members."
                );
            }

            var parsed = AzureAppConfigurationValueConverter.Parse(
                value,
                member.ValueType,
                memberPath,
                configurationKey,
                _options.JsonSerializerOptions,
                _options.ValueParser
            );
            return new AppliedFragment(fragment.WithMember(member.Id, parsed), true);
        }

        if (member.NestedSchemaFactory is null)
        {
            throw new FormatException(
                $"App Configuration key '{configurationKey}' continues past non-nested member '{member.Name}'."
            );
        }

        var nestedSchema = member.NestedSchemaFactory();
        var nestedFragment =
            FindPresentMember(fragment, member.Id) as IConfiglueFragment
            ?? nestedSchema.CreateEmptyFragment();
        var nestedResult = SetValue(
            nestedFragment,
            nestedSchema,
            path,
            pathIndex + 1,
            value,
            configurationKey,
            memberPath,
            cancellationToken
        );
        return nestedResult.Matched
            ? new AppliedFragment(fragment.WithMember(member.Id, nestedResult.Fragment), true)
            : new AppliedFragment(fragment, false);
    }

    private string[]? ResolveCanonicalPath(
        ConfiglueModelSchema schema,
        string[] path,
        string configurationKey
    )
    {
        var canonicalPath = new string[path.Length];
        for (var index = 0; index < path.Length; index++)
        {
            if (!_lookups[schema].TryResolve(path[index], out var member, out var isAmbiguous))
            {
                if (isAmbiguous)
                {
                    throw new FormatException(
                        $"App Configuration path segment '{path[index]}' is ambiguous in schema '{schema.Id}'."
                    );
                }

                return null;
            }

            canonicalPath[index] = member.Name;
            if (index == path.Length - 1)
            {
                return canonicalPath;
            }

            if (member.NestedSchemaFactory is null)
            {
                throw new FormatException(
                    $"App Configuration key '{configurationKey}' continues past non-nested member '{member.Name}'."
                );
            }

            schema = member.NestedSchemaFactory();
        }

        return null;
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

    private List<AppConfigurationEntry> KeepSelectable(
        IReadOnlyList<AppConfigurationEntry> entries
    ) =>
        entries
            .Where(entry =>
                !AzureAppConfigurationKeyMapper.IsFeatureFlag(entry)
                && AzureAppConfigurationKeyMapper.MatchesKeyFilter(entry.Key, _options.KeyFilter)
                && LabelMatches(entry.Label, _options.LabelFilter)
            )
            .ToList();

    private static bool LabelMatches(string? label, string? filter) =>
        string.Equals(label ?? string.Empty, filter ?? string.Empty, StringComparison.Ordinal);

    private static Dictionary<(string Key, string Label), AppConfigurationEntry> IndexByKey(
        List<AppConfigurationEntry> entries
    )
    {
        var index = new Dictionary<(string Key, string Label), AppConfigurationEntry>();
        foreach (var entry in entries)
        {
            index[(entry.Key, NormalizeLabel(entry.Label))] = entry;
        }

        return index;
    }

    private static string NormalizeLabel(string? label) => label ?? string.Empty;

    private static string SentinelRevision(AppConfigurationEntry? sentinel) =>
        sentinel is null
            ? "missing"
            : (sentinel.ETag ?? string.Empty) + "\n" + (sentinel.Value ?? string.Empty);

    internal static string CreateRevision(List<AppConfigurationEntry> entries)
    {
        var ordered = entries
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Label ?? string.Empty, StringComparer.Ordinal)
            .ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in ordered)
        {
            AppendHashedString(hash, entry.Key);
            AppendHashedString(hash, entry.Label ?? string.Empty);
            AppendHashedString(hash, entry.Value ?? string.Empty);
            AppendHashedString(hash, entry.ETag ?? string.Empty);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendHashedString(IncrementalHash hash, string value)
    {
        var lengthText =
            value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":";
        var prefixBytes = Encoding.UTF8.GetBytes(lengthText);
        var valueBytes = Encoding.UTF8.GetBytes(value);
#if NETSTANDARD
        hash.AppendData(prefixBytes);
        hash.AppendData(valueBytes);
#else
        hash.AppendData(prefixBytes.AsSpan());
        hash.AppendData(valueBytes.AsSpan());
#endif
    }

    private static IReadOnlyDictionary<ConfiglueModelSchema, SchemaMemberLookup> BuildLookups(
        ConfiglueModelSchema schema
    )
    {
        var lookups = new Dictionary<ConfiglueModelSchema, SchemaMemberLookup>();
        var ancestors = new HashSet<Type>();
        CollectLookups(schema, lookups, ancestors);
        return lookups;
    }

    private static void CollectLookups(
        ConfiglueModelSchema schema,
        Dictionary<ConfiglueModelSchema, SchemaMemberLookup> lookups,
        HashSet<Type> ancestors
    )
    {
        if (!ancestors.Add(schema.ModelType))
        {
            return;
        }

        lookups[schema] = SchemaMemberLookup.Create(schema);
        for (var index = 0; index < schema.Members.Count; index++)
        {
            var member = schema.Members[index];
            var factory = member.NestedSchemaFactory;
            if (factory is null)
            {
                continue;
            }

            CollectLookups(factory(), lookups, ancestors);
        }

        ancestors.Remove(schema.ModelType);
    }

    private static string CreateIdentityHash(AzureAppConfigurationSourceOptions options)
    {
        var host = DescribeEndpoint(options);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashedString(hash, host);
        AppendHashedString(hash, options.KeyFilter);
        AppendHashedString(hash, options.LabelFilter ?? string.Empty);
        AppendHashedString(hash, options.TrimKeyPrefix ?? string.Empty);
        AppendHashedString(hash, options.SnapshotName ?? string.Empty);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string DescribeEndpoint(AzureAppConfigurationSourceOptions options)
    {
        if (options.Endpoint is { } endpoint)
        {
            return endpoint.Host;
        }

        if (options.ConnectionString is { } connectionString)
        {
            foreach (var part in connectionString.Split(';'))
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith("Endpoint=", StringComparison.OrdinalIgnoreCase))
                {
                    var value = trimmed["Endpoint=".Length..].Trim();
                    if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
                    {
                        return uri.Host;
                    }

                    return "connection-string";
                }
            }

            return "connection-string";
        }

        return options.SnapshotName is not null ? "snapshot" : "injected-client";
    }

    private readonly record struct Assignment(string[] Path, string Value, string ConfigurationKey);

    private readonly record struct AppliedFragment(IConfiglueFragment Fragment, bool Matched);

    private sealed record PlannedWrite(string Key, string? Label, string Value);
}
