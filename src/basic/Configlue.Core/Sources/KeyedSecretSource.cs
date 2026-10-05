using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Configlue.Codecs;
using Configlue.CompilerServices;
using Configlue.Internal;
using Configlue.Resources;
using Configlue.Sources;

namespace Configlue.State;

/// <summary>
/// Reads sparse fragments from explicitly mapped keys in a keyed secret store,
/// with optional convention mapping. Shared by secret store providers
/// (Azure Key Vault, SSM Parameter Store, and similar) so mapping, scalar/JSON
/// conversion, revision, and polling behavior stay consistent.
/// </summary>
/// <remarks>
/// <para>Secret values never appear in revisions, provenance, or exception messages.</para>
/// <para>Writes are opt-in and unconditional; conditional writes are rejected.</para>
/// </remarks>
/// <remarks>Advanced composition SPI: the shared keyed-secret source behind secret store providers.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class KeyedSecretSource<TFragment>
    : ISourceWriter<TFragment>,
        ISourceWatcher,
        ISourceCapabilities<TFragment>,
        IDisposable
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly IKeyedSecretClient _client;
    private readonly ConfiglueModelSchema _schema;
    private readonly IReadOnlyList<ResolvedMapping> _mappings;
    private readonly bool _writable;
    private readonly TimeSpan? _pollInterval;
    private readonly Func<string, Type, object?>? _valueParser;
    private readonly JsonSerializerOptions? _jsonOptions;
    private readonly string? _physicalOrigin;
    private readonly Dictionary<ConfiglueModelSchema, KeyedSchemaLookup> _lookups;
    private readonly WatchShutdown _watchShutdown = new();
    private int _disposed;

    /// <summary>Creates a keyed-secret source for the supplied schema.</summary>
    public KeyedSecretSource(
        IKeyedSecretClient client,
        ConfiglueModelSchema schema,
        IReadOnlyList<KeyedSecretMapping> explicitMappings,
        KeyedSecretSourceOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(explicitMappings);
        options ??= new KeyedSecretSourceOptions();
        if (options.ConventionSeparator is null or { Length: 0 })
        {
            throw new ArgumentException(
                "A convention separator must be non-empty.",
                nameof(options)
            );
        }

        if (options.PollInterval is { } pollInterval)
        {
            PollingWatch.ValidateInterval(pollInterval, nameof(options));
        }

        _client = client;
        _schema = schema;
        _mappings = ResolveMappings(schema, explicitMappings, options);
        _writable = options.Writable;
        _pollInterval = options.PollInterval;
        _valueParser = options.ValueParser;
        _jsonOptions = options.JsonSerializerOptions;
        _physicalOrigin = options.PhysicalOrigin;
        _lookups = BuildLookups(schema);
    }

    /// <inheritdoc />
    public ISourceWriter<TFragment>? Writer => _writable ? this : null;

    /// <inheritdoc />
    public ISourceWatcher? Watcher => _pollInterval is not null && !IsAllFixedVersion ? this : null;

    /// <summary>Whether every mapping pins a fixed version. Fixed-version sources are immutable and expose no watcher.</summary>
    public bool IsAllFixedVersion =>
        _mappings.Count > 0 && _mappings.All(static m => m.EffectiveVersion is not null);

    /// <summary>The resolved member-to-key mappings.</summary>
    public IReadOnlyList<ResolvedMapping> ResolvedMappings => _mappings;

    /// <inheritdoc />
    public async ValueTask<StateReadResult<TFragment>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        _ = context;
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        var fetched = new List<FetchedSecret>(_mappings.Count);
        var unavailable = false;
        foreach (var mapping in _mappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            KeyedSecretValue? secret;
            try
            {
                secret = await _client
                    .GetAsync(mapping.Key, mapping.EffectiveVersion, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (KeyedSecretUnavailableException)
            {
                unavailable = true;
                fetched.Add(new FetchedSecret(mapping, null, Unavailable: true));
                continue;
            }

            if (secret is null || !secret.Enabled)
            {
                fetched.Add(new FetchedSecret(mapping, null));
                continue;
            }

            fetched.Add(new FetchedSecret(mapping, secret));
        }

        var revision = CreateRevision(fetched);
        if (unavailable)
        {
            // A partial transient failure cannot produce a trustworthy sparse fragment.
            return StateReadResult<TFragment>.Unavailable(revision) with
            {
                PhysicalOrigin = _physicalOrigin,
            };
        }

        IConfiglueFragment fragment = _schema.CreateEmptyFragment();
        var matchedAny = false;
        foreach (var entry in fetched)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Secret is null)
            {
                continue;
            }

            var segments = entry.Mapping.PropertyPathSegments;
            object? parsed;
            try
            {
                parsed = ParseValue(
                    entry.Secret.Value,
                    entry.Mapping.LeafType,
                    entry.Mapping.PropertyPath,
                    entry.Mapping.Key
                );
            }
            catch (FormatException)
            {
                return StateReadResult<TFragment>.InvalidPayload(null, revision) with
                {
                    PhysicalOrigin = _physicalOrigin,
                };
            }

            try
            {
                var applied = SetValue(fragment, _schema, segments, 0, parsed);
                fragment = applied.Fragment;
                matchedAny |= applied.Matched;
            }
            catch (FormatException)
            {
                return StateReadResult<TFragment>.InvalidPayload(null, revision) with
                {
                    PhysicalOrigin = _physicalOrigin,
                };
            }
        }

        if (!matchedAny)
        {
            return StateReadResult<TFragment>.NotFound(revision) with
            {
                PhysicalOrigin = _physicalOrigin,
            };
        }

        var typed = (TFragment)fragment;
        return StateReadResult<TFragment>.Success(typed, revision, _schema.ToMetadata()) with
        {
            PhysicalOrigin = _physicalOrigin,
        };
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<TFragment> request,
        CancellationToken cancellationToken = default
    )
    {
        _ = context;
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_writable)
        {
            throw new InvalidOperationException(
                "This keyed-secret source is read-only. Enable opt-in writes explicitly."
            );
        }

        if (request.Condition is { IsNone: false })
        {
            throw new InvalidOperationException(
                "Keyed secrets do not support conditional writes. "
                    + "The store upsert is unconditional with no compare-and-swap; "
                    + "retry with an unchecked write or use a store with CAS support."
            );
        }

        var fragment = (IConfiglueFragment)request.Value;
        var writes = CollectWrites(fragment);
        if (writes.Count == 0)
        {
            return new StateWriteResult(null);
        }

        var versions = new List<(string Key, string? Version)>(writes.Count);
        foreach (var write in writes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? version;
            try
            {
                version = await _client
                    .SetAsync(write.Key, write.Value, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (KeyedSecretUnavailableException exception)
            {
                throw new InvalidOperationException(
                    $"The secret '{write.Key}' is temporarily unavailable.",
                    exception
                );
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"The secret '{write.Key}' could not be written.",
                    exception
                );
            }

            versions.Add((write.Key, version));
        }

        return new StateWriteResult(CreateRevisionFromVersions(versions));
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
        if (_pollInterval is null || IsAllFixedVersion)
        {
            throw new InvalidOperationException(
                "This keyed-secret source does not support watching."
            );
        }

        await _watchShutdown
            .WaitAsync(
                watchCancellationToken =>
                    PollingWatch.WaitForRevisionChangeAsync(
                        GetRevisionAsync,
                        observedRevision,
                        _pollInterval.Value,
                        watchCancellationToken,
                        static exception => exception is KeyedSecretUnavailableException
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

    /// <inheritdoc />
    public override string ToString() =>
        $"KeyedSecretSource(origin={_physicalOrigin ?? "<unknown>"}, keys={_mappings.Count}, writable={_writable})";

    internal async ValueTask<string?> GetRevisionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var fetched = new List<FetchedSecret>(_mappings.Count);
        foreach (var mapping in _mappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var secret = await _client
                .GetAsync(mapping.Key, mapping.EffectiveVersion, cancellationToken)
                .ConfigureAwait(false);
            fetched.Add(
                secret is not null && secret.Enabled
                    ? new FetchedSecret(mapping, secret)
                    : new FetchedSecret(mapping, null)
            );
        }

        return CreateRevision(fetched);
    }

    internal static string CreateRevision(IReadOnlyList<FetchedSecret> fetched)
    {
        var ordered = fetched
            .Select(static f => (f.Mapping.Key, Version: f.Secret?.Version ?? "<missing>"))
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToArray();
        using var hash = SHA256.Create();
        foreach (var (key, version) in ordered)
        {
            var keyBytes = Encoding.UTF8.GetBytes(key.Length + ":" + key);
            hash.TransformBlock(keyBytes, 0, keyBytes.Length, null, 0);
            var versionBytes = Encoding.UTF8.GetBytes(version.Length + ":" + version);
            hash.TransformBlock(versionBytes, 0, versionBytes.Length, null, 0);
        }

        hash.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(hash.Hash ?? []).ToUpperInvariant();
    }

    internal static string CreateRevisionFromVersions(
        IReadOnlyList<(string Key, string? Version)> versions
    )
    {
        var ordered = versions
            .Select(static v => (v.Key, Version: v.Version ?? "<missing>"))
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToArray();
        using var hash = SHA256.Create();
        foreach (var (key, version) in ordered)
        {
            var keyBytes = Encoding.UTF8.GetBytes(key.Length + ":" + key);
            hash.TransformBlock(keyBytes, 0, keyBytes.Length, null, 0);
            var versionBytes = Encoding.UTF8.GetBytes(version.Length + ":" + version);
            hash.TransformBlock(versionBytes, 0, versionBytes.Length, null, 0);
        }

        hash.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(hash.Hash ?? []).ToUpperInvariant();
    }

    /// <summary>A resolved member-to-key mapping.</summary>
    public sealed record ResolvedMapping(
        string PropertyPath,
        string[] PropertyPathSegments,
        string Key,
        string? EffectiveVersion,
        Type LeafType
    )
    {
        /// <inheritdoc />
        public override string ToString() =>
            $"KeyedSecretMapping(path={PropertyPath}, key={Key}, version={EffectiveVersion ?? "<current>"})";
    }

    internal sealed record FetchedSecret(
        ResolvedMapping Mapping,
        KeyedSecretValue? Secret,
        bool Unavailable = false
    );

    internal sealed record SecretWrite(string Key, string Value);

    /// <summary>Resolves explicit mappings and optional convention keys against the model schema.</summary>
    public static IReadOnlyList<ResolvedMapping> ResolveMappings(
        ConfiglueModelSchema schema,
        IReadOnlyList<KeyedSecretMapping> explicitMappings,
        KeyedSecretSourceOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(explicitMappings);
        options ??= new KeyedSecretSourceOptions();
        if (options.ConventionSeparator is null or { Length: 0 })
        {
            throw new ArgumentException(
                "A convention separator must be non-empty.",
                nameof(options)
            );
        }

        var lookups = BuildLookups(schema);
        var resolved = new Dictionary<string, ResolvedMapping>(StringComparer.OrdinalIgnoreCase);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var mapping in explicitMappings)
        {
            ArgumentNullException.ThrowIfNull(mapping);
            var key = options.KeyValidator?.Invoke(mapping.Key) ?? mapping.Key;
            var segments = SplitPath(mapping.PropertyPath);
            var leafType = ResolveLeafType(schema, lookups, segments, mapping.PropertyPath);
            var effectiveVersion = mapping.Version ?? options.DefaultVersion;
            var entry = new ResolvedMapping(
                string.Join(".", segments),
                segments,
                key,
                effectiveVersion,
                leafType
            );
            if (!resolved.TryAdd(entry.PropertyPath, entry))
            {
                throw new ArgumentException(
                    $"More than one mapping targets member '{entry.PropertyPath}'.",
                    nameof(explicitMappings)
                );
            }

            if (!keys.TryAdd(entry.Key, entry.PropertyPath))
            {
                throw new ArgumentException(
                    $"More than one mapping targets key '{entry.Key}'.",
                    nameof(explicitMappings)
                );
            }
        }

        if (options.EnableConventionMapping)
        {
            if (options.ConventionPrefix is not null)
            {
                _ = options.KeyValidator?.Invoke(options.ConventionPrefix);
            }

            foreach (var leaf in EnumerateLeafPaths(schema, lookups, []))
            {
                if (resolved.ContainsKey(leaf.PropertyPath))
                {
                    continue;
                }

                var conventionKey = options.ConventionPrefix is null
                    ? string.Join(options.ConventionSeparator, leaf.Segments)
                    : options.ConventionPrefix
                        + options.ConventionSeparator
                        + string.Join(options.ConventionSeparator, leaf.Segments);
                var validated = options.KeyValidator?.Invoke(conventionKey) ?? conventionKey;
                if (!keys.TryAdd(validated, leaf.PropertyPath))
                {
                    throw new ArgumentException(
                        $"The convention key '{validated}' is ambiguous.",
                        nameof(options)
                    );
                }

                resolved.Add(
                    leaf.PropertyPath,
                    new ResolvedMapping(
                        leaf.PropertyPath,
                        leaf.Segments,
                        validated,
                        options.DefaultVersion,
                        leaf.LeafType
                    )
                );
            }
        }

        if (resolved.Count == 0)
        {
            throw new ArgumentException(
                "Configure at least one explicit mapping or enable convention mapping.",
                nameof(explicitMappings)
            );
        }

        return resolved
            .Values.OrderBy(static m => m.PropertyPath, StringComparer.Ordinal)
            .ToArray();
    }

    private static Dictionary<ConfiglueModelSchema, KeyedSchemaLookup> BuildLookups(
        ConfiglueModelSchema schema
    )
    {
        var lookups = new Dictionary<ConfiglueModelSchema, KeyedSchemaLookup>();
        CollectLookups(schema, new HashSet<Type>(), lookups);
        return lookups;
    }

    private static void CollectLookups(
        ConfiglueModelSchema schema,
        HashSet<Type> ancestors,
        Dictionary<ConfiglueModelSchema, KeyedSchemaLookup> lookups
    )
    {
        if (!ancestors.Add(schema.ModelType))
        {
            return;
        }

        lookups[schema] = KeyedSchemaLookup.Create(schema);
        for (var index = 0; index < schema.Members.Count; index++)
        {
            var nestedFactory = schema.Members[index].NestedSchemaFactory;
            if (nestedFactory is null)
            {
                continue;
            }

            CollectLookups(nestedFactory(), ancestors, lookups);
        }

        ancestors.Remove(schema.ModelType);
    }

    private static string[] SplitPath(string propertyPath)
    {
        var segments = propertyPath.Split('.');
        if (segments.Length == 0 || segments.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                $"Mapping '{propertyPath}' must contain non-empty '.'-separated segments.",
                nameof(propertyPath)
            );
        }

        return segments.Select(static s => s.Trim()).ToArray();
    }

    private static Type ResolveLeafType(
        ConfiglueModelSchema schema,
        Dictionary<ConfiglueModelSchema, KeyedSchemaLookup> lookups,
        string[] segments,
        string propertyPath
    )
    {
        var current = schema;
        for (var index = 0; index < segments.Length; index++)
        {
            if (!lookups[current].TryResolve(segments[index], out var member, out var isAmbiguous))
            {
                if (isAmbiguous)
                {
                    throw new ArgumentException(
                        $"Mapping '{propertyPath}' segment '{segments[index]}' is ambiguous in schema '{current.Id}'.",
                        nameof(propertyPath)
                    );
                }

                throw new ArgumentException(
                    $"Mapping '{propertyPath}' does not match schema '{current.Id}'.",
                    nameof(propertyPath)
                );
            }

            segments[index] = member.Name;
            if (index == segments.Length - 1)
            {
                if (member.NestedSchemaFactory is not null)
                {
                    throw new ArgumentException(
                        $"Mapping '{propertyPath}' names a nested model. Map its leaf members instead.",
                        nameof(propertyPath)
                    );
                }

                return member.ValueType;
            }

            if (member.NestedSchemaFactory is null)
            {
                throw new ArgumentException(
                    $"Mapping '{propertyPath}' continues past non-nested member '{member.Name}'.",
                    nameof(propertyPath)
                );
            }

            current = member.NestedSchemaFactory();
        }

        throw new ArgumentException($"Mapping '{propertyPath}' is empty.", nameof(propertyPath));
    }

    private static IEnumerable<(
        string PropertyPath,
        string[] Segments,
        Type LeafType
    )> EnumerateLeafPaths(
        ConfiglueModelSchema schema,
        Dictionary<ConfiglueModelSchema, KeyedSchemaLookup> lookups,
        string[] parentPath
    )
    {
        foreach (var member in schema.Members)
        {
            var path = parentPath.Append(member.Name).ToArray();
            if (member.NestedSchemaFactory is null)
            {
                yield return (string.Join(".", path), path, member.ValueType);
            }
            else
            {
                foreach (
                    var nested in EnumerateLeafPaths(member.NestedSchemaFactory(), lookups, path)
                )
                {
                    yield return nested;
                }
            }
        }
    }

    private AppliedFragment SetValue(
        IConfiglueFragment fragment,
        ConfiglueModelSchema schema,
        string[] path,
        int pathIndex,
        object? value
    )
    {
        if (!_lookups[schema].TryResolve(path[pathIndex], out var member, out var isAmbiguous))
        {
            if (isAmbiguous)
            {
                throw new FormatException(
                    $"Path segment '{path[pathIndex]}' is ambiguous in schema '{schema.Id}'."
                );
            }

            return new AppliedFragment(fragment, false);
        }

        if (pathIndex == path.Length - 1)
        {
            if (member.NestedSchemaFactory is not null)
            {
                throw new FormatException($"Mapping names nested model '{member.Name}'.");
            }

            return new AppliedFragment(fragment.WithMember(member.Id, value), true);
        }

        if (member.NestedSchemaFactory is null)
        {
            throw new FormatException($"Mapping continues past non-nested member '{member.Name}'.");
        }

        var nestedSchema = member.NestedSchemaFactory();
        var nestedFragment =
            FindPresentMember(fragment, member.Id) as IConfiglueFragment
            ?? nestedSchema.CreateEmptyFragment();
        var nestedResult = SetValue(nestedFragment, nestedSchema, path, pathIndex + 1, value);
        return nestedResult.Matched
            ? new AppliedFragment(fragment.WithMember(member.Id, nestedResult.Fragment), true)
            : new AppliedFragment(fragment, false);
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

    private List<SecretWrite> CollectWrites(IConfiglueFragment fragment)
    {
        var byPath = _mappings.ToDictionary(
            static m => m.PropertyPath,
            static m => m,
            StringComparer.OrdinalIgnoreCase
        );
        var writes = new List<SecretWrite>();
        CollectWritesCore(fragment, _schema, [], byPath, writes);
        return writes;
    }

    private void CollectWritesCore(
        IConfiglueFragment fragment,
        ConfiglueModelSchema schema,
        string[] parentPath,
        Dictionary<string, ResolvedMapping> byPath,
        List<SecretWrite> writes
    )
    {
        foreach (var member in fragment.EnumeratePresentMembers())
        {
            if (!schema.TryGetMember(member.Id, out var memberSchema))
            {
                continue;
            }

            var path = parentPath.Append(memberSchema.Name).ToArray();
            var dotted = string.Join(".", path);
            if (memberSchema.NestedSchemaFactory is not null)
            {
                if (member.Value is IConfiglueFragment nested)
                {
                    CollectWritesCore(
                        nested,
                        memberSchema.NestedSchemaFactory(),
                        path,
                        byPath,
                        writes
                    );
                }

                continue;
            }

            if (byPath.TryGetValue(dotted, out var mapping))
            {
                writes.Add(
                    new SecretWrite(
                        mapping.Key,
                        ScalarTextConverter.Format(
                            member.Value,
                            memberSchema.ValueType,
                            _jsonOptions
                        )
                    )
                );
            }
        }
    }

    private object? ParseValue(string value, Type targetType, string propertyPath, string key)
    {
        object? parsed;
        try
        {
            parsed = _valueParser is not null
                ? ScalarTextConverter.Parse(value, targetType, _valueParser, _jsonOptions)
                : ScalarTextConverter.Parse(value, targetType, null, _jsonOptions);
        }
        catch (FormatException exception)
        {
            throw new FormatException(
                $"The secret '{key}' is not a valid value for member '{propertyPath}'.",
                exception
            );
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new FormatException(
                $"The secret '{key}' is not a valid value for member '{propertyPath}'.",
                exception
            );
        }

        return parsed;
    }

    private readonly record struct AppliedFragment(IConfiglueFragment Fragment, bool Matched);

    private sealed class KeyedSchemaLookup
    {
        private readonly Dictionary<string, ConfiglueMemberSchema> _membersByName;
        private readonly HashSet<string> _ambiguousNames;

        private KeyedSchemaLookup(
            Dictionary<string, ConfiglueMemberSchema> membersByName,
            HashSet<string> ambiguousNames
        )
        {
            _membersByName = membersByName;
            _ambiguousNames = ambiguousNames;
        }

        public static KeyedSchemaLookup Create(ConfiglueModelSchema schema)
        {
            ArgumentNullException.ThrowIfNull(schema);
            var membersByName = new Dictionary<string, ConfiglueMemberSchema>(
                StringComparer.OrdinalIgnoreCase
            );
            var ambiguousNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in schema.Members)
            {
                if (ambiguousNames.Contains(member.Name))
                {
                    continue;
                }

                if (!membersByName.TryAdd(member.Name, member))
                {
                    ambiguousNames.Add(member.Name);
                    membersByName.Remove(member.Name);
                }
            }

            return new KeyedSchemaLookup(membersByName, ambiguousNames);
        }

        public bool TryResolve(
            string segment,
            out ConfiglueMemberSchema member,
            out bool isAmbiguous
        )
        {
            if (_ambiguousNames.Contains(segment))
            {
                member = default;
                isAmbiguous = true;
                return false;
            }

            if (_membersByName.TryGetValue(segment, out member))
            {
                isAmbiguous = false;
                return true;
            }

            member = default;
            isAmbiguous = false;
            return false;
        }
    }
}
