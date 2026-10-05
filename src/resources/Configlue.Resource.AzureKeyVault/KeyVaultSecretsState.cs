using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Configlue.CompilerServices;
using Configlue.Internal;

namespace Configlue.Resource.AzureKeyVault;

/// <summary>
/// Reads sparse fragments from explicitly mapped Key Vault secrets, with optional convention mapping.
/// </summary>
/// <remarks>
/// <para>Secret values never appear in revisions, provenance, or exception messages.</para>
/// <para>Writes are opt-in and unconditional; conditional writes are rejected.</para>
/// </remarks>
public sealed class KeyVaultSecretsState<TFragment>
    : ISourceWriter<TFragment>,
        ISourceWatcher,
        ISourceCapabilities<TFragment>,
        IDisposable
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly IKeyVaultSecretClient _client;
    private readonly Uri _vaultUri;
    private readonly ConfiglueModelSchema _schema;
    private readonly IReadOnlyList<ResolvedMapping> _mappings;
    private readonly bool _writable;
    private readonly TimeSpan? _pollInterval;
    private readonly Func<string, Type, object?>? _valueParser;
    private readonly JsonSerializerOptions? _jsonOptions;
    private readonly string _physicalOrigin;
    private readonly Dictionary<ConfiglueModelSchema, KeyVaultSchemaLookup> _lookups;
    private readonly WatchShutdown _watchShutdown = new();
    private int _disposed;

    internal KeyVaultSecretsState(
        IKeyVaultSecretClient client,
        Uri vaultUri,
        ConfiglueModelSchema schema,
        IReadOnlyList<ResolvedMapping> mappings,
        bool writable,
        TimeSpan? pollInterval,
        Func<string, Type, object?>? valueParser,
        JsonSerializerOptions? jsonOptions
    )
    {
        _client = client;
        _vaultUri = vaultUri;
        _schema = schema;
        _mappings = mappings;
        _writable = writable;
        _pollInterval = pollInterval;
        _valueParser = valueParser;
        _jsonOptions = jsonOptions;
        _physicalOrigin = KeyVaultClients.GetPhysicalOrigin(vaultUri);
        _lookups = BuildLookups(schema);
    }

    /// <inheritdoc />
    public ISourceWriter<TFragment>? Writer => _writable ? this : null;

    /// <inheritdoc />
    public ISourceWatcher? Watcher => _pollInterval is not null && !IsAllFixedVersion ? this : null;

    internal bool IsAllFixedVersion =>
        _mappings.Count > 0 && _mappings.All(static m => m.EffectiveVersion is not null);

    internal string PhysicalOrigin => _physicalOrigin;

    internal IReadOnlyList<ResolvedMapping> ResolvedMappings => _mappings;

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
            KeyVaultSecretResult secret;
            try
            {
                secret = await _client
                    .GetSecretAsync(mapping.SecretName, mapping.EffectiveVersion, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (KeyVaultSecretNotFoundException)
            {
                fetched.Add(new FetchedSecret(mapping, null));
                continue;
            }
            catch (KeyVaultSecretUnavailableException)
            {
                unavailable = true;
                fetched.Add(new FetchedSecret(mapping, null, Unavailable: true));
                continue;
            }

            if (!secret.Metadata.Enabled)
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
                    entry.Mapping.SecretName
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
                "This Key Vault secrets source is read-only. Enable opt-in writes explicitly."
            );
        }

        if (request.Condition is { IsNone: false })
        {
            throw new InvalidOperationException(
                "Key Vault secrets do not support conditional writes. "
                    + "SetSecret is an unconditional upsert with no compare-and-swap; "
                    + "retry with an unchecked write or use a store with CAS support."
            );
        }

        var fragment = (IConfiglueFragment)request.Value;
        var writes = CollectWrites(fragment);
        if (writes.Count == 0)
        {
            return new StateWriteResult(null);
        }

        var versions = new List<(string Name, string? Version)>(writes.Count);
        foreach (var write in writes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            KeyVaultSecretMetadata metadata;
            try
            {
                metadata = await _client
                    .SetSecretAsync(write.SecretName, write.Value, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (KeyVaultSecretUnavailableException exception)
            {
                throw new InvalidOperationException(
                    $"The Key Vault secret '{write.SecretName}' is temporarily unavailable.",
                    exception
                );
            }
            catch (KeyVaultSecretException exception)
            {
                throw new InvalidOperationException(
                    $"The Key Vault secret '{write.SecretName}' could not be written.",
                    exception
                );
            }

            versions.Add((write.SecretName, metadata.Version));
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
                "This Key Vault secrets source does not support watching."
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
                        static exception => exception is KeyVaultSecretUnavailableException
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
        $"KeyVaultSecretsState(vault={_vaultUri.Host}, mappings={_mappings.Count}, writable={_writable})";

    internal async ValueTask<string?> GetRevisionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var fetched = new List<FetchedSecret>(_mappings.Count);
        foreach (var mapping in _mappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var secret = await _client
                    .GetSecretAsync(mapping.SecretName, mapping.EffectiveVersion, cancellationToken)
                    .ConfigureAwait(false);
                fetched.Add(
                    secret.Metadata.Enabled
                        ? new FetchedSecret(mapping, secret)
                        : new FetchedSecret(mapping, null)
                );
            }
            catch (KeyVaultSecretNotFoundException)
            {
                fetched.Add(new FetchedSecret(mapping, null));
            }
        }

        return CreateRevision(fetched);
    }

    internal static string CreateRevision(IReadOnlyList<FetchedSecret> fetched)
    {
        var ordered = fetched
            .Select(static f =>
                (f.Mapping.SecretName, Version: f.Secret?.Metadata.Version ?? "<missing>")
            )
            .OrderBy(static pair => pair.SecretName, StringComparer.Ordinal)
            .ToArray();
        using var hash = System.Security.Cryptography.SHA256.Create();
        foreach (var (name, version) in ordered)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name.Length + ":" + name);
            hash.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
            var versionBytes = Encoding.UTF8.GetBytes(version.Length + ":" + version);
            hash.TransformBlock(versionBytes, 0, versionBytes.Length, null, 0);
        }

        hash.TransformFinalBlock([], 0, 0);
        var computed = hash.Hash ?? [];
        var builder = new StringBuilder(computed.Length * 2);
        foreach (var b in computed)
        {
            builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString().ToUpperInvariant();
    }

    internal static string CreateRevisionFromVersions(
        IReadOnlyList<(string Name, string? Version)> versions
    )
    {
        var ordered = versions
            .Select(static v => (v.Name, Version: v.Version ?? "<missing>"))
            .OrderBy(static pair => pair.Name, StringComparer.Ordinal)
            .ToArray();
        using var hash = SHA256.Create();
        foreach (var (name, version) in ordered)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name.Length + ":" + name);
            hash.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
            var versionBytes = Encoding.UTF8.GetBytes(version.Length + ":" + version);
            hash.TransformBlock(versionBytes, 0, versionBytes.Length, null, 0);
        }

        hash.TransformFinalBlock([], 0, 0);
        var computed = hash.Hash ?? [];
        var builder = new StringBuilder(computed.Length * 2);
        foreach (var b in computed)
        {
            builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString().ToUpperInvariant();
    }

    internal sealed record ResolvedMapping(
        string PropertyPath,
        string[] PropertyPathSegments,
        string SecretName,
        string? EffectiveVersion,
        Type LeafType
    )
    {
        public override string ToString() =>
            $"KeyVaultSecretMapping(path={PropertyPath}, secret={SecretName}, version={EffectiveVersion ?? "<current>"})";
    }

    internal sealed record FetchedSecret(
        ResolvedMapping Mapping,
        KeyVaultSecretResult? Secret,
        bool Unavailable = false
    );

    internal static IReadOnlyList<ResolvedMapping> ResolveMappings(
        ConfiglueModelSchema schema,
        IReadOnlyList<KeyVaultSecretMapping> explicitMappings,
        bool enableConvention,
        string? conventionPrefix,
        string? defaultVersion
    )
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(explicitMappings);
        var lookups = BuildLookups(schema);
        var resolved = new Dictionary<string, ResolvedMapping>(StringComparer.OrdinalIgnoreCase);
        var secretNames = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var mapping in explicitMappings)
        {
            var segments = SplitPath(mapping.PropertyPath);
            var leafType = ResolveLeafType(schema, lookups, segments, mapping.PropertyPath);
            var effectiveVersion = mapping.Version ?? defaultVersion;
            var entry = new ResolvedMapping(
                string.Join(".", segments),
                segments,
                mapping.SecretName,
                effectiveVersion,
                leafType
            );
            if (!resolved.TryAdd(entry.PropertyPath, entry))
            {
                throw new ArgumentException(
                    $"More than one Key Vault mapping targets member '{entry.PropertyPath}'.",
                    nameof(explicitMappings)
                );
            }

            if (!secretNames.TryAdd(entry.SecretName, entry.PropertyPath))
            {
                throw new ArgumentException(
                    $"More than one Key Vault mapping targets secret '{entry.SecretName}'.",
                    nameof(explicitMappings)
                );
            }
        }

        if (enableConvention)
        {
            if (conventionPrefix is not null)
            {
                KeyVaultSecretName.Validate(conventionPrefix, nameof(conventionPrefix));
            }

            foreach (var leaf in EnumerateLeafPaths(schema, lookups, []))
            {
                if (resolved.ContainsKey(leaf.PropertyPath))
                {
                    continue;
                }

                var conventionName = KeyVaultSecretName.ToConventionSecretName(
                    leaf.PropertyPath,
                    conventionPrefix
                );
                if (!secretNames.TryAdd(conventionName, leaf.PropertyPath))
                {
                    throw new ArgumentException(
                        $"The convention secret name '{conventionName}' is ambiguous.",
                        nameof(conventionPrefix)
                    );
                }

                resolved.Add(
                    leaf.PropertyPath,
                    new ResolvedMapping(
                        leaf.PropertyPath,
                        leaf.Segments,
                        conventionName,
                        defaultVersion,
                        leaf.LeafType
                    )
                );
            }
        }

        if (resolved.Count == 0)
        {
            throw new ArgumentException(
                "Configure at least one explicit Key Vault secret mapping or enable convention mapping.",
                nameof(explicitMappings)
            );
        }

        return resolved
            .Values.OrderBy(static m => m.PropertyPath, StringComparer.Ordinal)
            .ToArray();
    }

    private static Dictionary<ConfiglueModelSchema, KeyVaultSchemaLookup> BuildLookups(
        ConfiglueModelSchema schema
    )
    {
        var lookups = new Dictionary<ConfiglueModelSchema, KeyVaultSchemaLookup>();
        CollectLookups(schema, new HashSet<Type>(), lookups);
        return lookups;
    }

    private static void CollectLookups(
        ConfiglueModelSchema schema,
        HashSet<Type> ancestors,
        Dictionary<ConfiglueModelSchema, KeyVaultSchemaLookup> lookups
    )
    {
        if (!ancestors.Add(schema.ModelType))
        {
            return;
        }

        lookups[schema] = KeyVaultSchemaLookup.Create(schema);
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
                $"Key Vault mapping '{propertyPath}' must contain non-empty '.'-separated segments.",
                nameof(propertyPath)
            );
        }

        return segments.Select(static s => s.Trim()).ToArray();
    }

    private static Type ResolveLeafType(
        ConfiglueModelSchema schema,
        Dictionary<ConfiglueModelSchema, KeyVaultSchemaLookup> lookups,
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
                        $"Key Vault mapping '{propertyPath}' segment '{segments[index]}' is ambiguous in schema '{current.Id}'.",
                        nameof(propertyPath)
                    );
                }

                throw new ArgumentException(
                    $"Key Vault mapping '{propertyPath}' does not match schema '{current.Id}'.",
                    nameof(propertyPath)
                );
            }

            segments[index] = member.Name;
            if (index == segments.Length - 1)
            {
                if (member.NestedSchemaFactory is not null)
                {
                    throw new ArgumentException(
                        $"Key Vault mapping '{propertyPath}' names a nested model. Map its leaf members instead.",
                        nameof(propertyPath)
                    );
                }

                return member.ValueType;
            }

            if (member.NestedSchemaFactory is null)
            {
                throw new ArgumentException(
                    $"Key Vault mapping '{propertyPath}' continues past non-nested member '{member.Name}'.",
                    nameof(propertyPath)
                );
            }

            current = member.NestedSchemaFactory();
        }

        throw new ArgumentException(
            $"Key Vault mapping '{propertyPath}' is empty.",
            nameof(propertyPath)
        );
    }

    private static IEnumerable<(
        string PropertyPath,
        string[] Segments,
        Type LeafType
    )> EnumerateLeafPaths(
        ConfiglueModelSchema schema,
        Dictionary<ConfiglueModelSchema, KeyVaultSchemaLookup> lookups,
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
                    $"Key Vault path segment '{path[pathIndex]}' is ambiguous in schema '{schema.Id}'."
                );
            }

            return new AppliedFragment(fragment, false);
        }

        if (pathIndex == path.Length - 1)
        {
            if (member.NestedSchemaFactory is not null)
            {
                throw new FormatException($"Key Vault mapping names nested model '{member.Name}'.");
            }

            return new AppliedFragment(fragment.WithMember(member.Id, value), true);
        }

        if (member.NestedSchemaFactory is null)
        {
            throw new FormatException(
                $"Key Vault mapping continues past non-nested member '{member.Name}'."
            );
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
                        mapping.SecretName,
                        FormatValue(member.Value, memberSchema.ValueType)
                    )
                );
            }
        }
    }

    private object? ParseValue(
        string value,
        Type targetType,
        string propertyPath,
        string secretName
    )
    {
        object? parsed;
        try
        {
            parsed = _valueParser is not null
                ? _valueParser(value, targetType)
                : ParseScalar(value, targetType);
        }
        catch (NotSupportedException exception)
        {
            parsed = ParseJson(value, targetType, exception, propertyPath, secretName);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new FormatException(
                $"The Key Vault secret '{secretName}' is not a valid value for member '{propertyPath}'.",
                exception
            );
        }

        var valueType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (parsed is null)
        {
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null)
            {
                throw new FormatException(
                    $"The parser returned null for non-nullable member '{propertyPath}'."
                );
            }

            return null;
        }

        if (!valueType.IsInstanceOfType(parsed))
        {
            throw new FormatException(
                $"The parser returned an incompatible value for member '{propertyPath}'."
            );
        }

        return parsed;
    }

    private object? ParseScalar(string value, Type targetType)
    {
        var nullableType = Nullable.GetUnderlyingType(targetType);
        var valueType = nullableType ?? targetType;
        if (nullableType is not null && value.Length == 0)
        {
            return null;
        }

        if (valueType == typeof(string))
        {
            return value;
        }
        if (valueType == typeof(bool))
        {
            return bool.Parse(value);
        }
        if (valueType == typeof(char))
        {
            return value.Length == 1
                ? value[0]
                : throw new FormatException(
                    "A character secret value must contain exactly one character."
                );
        }
        if (valueType.IsEnum)
        {
            return Enum.Parse(valueType, value, ignoreCase: true);
        }
        if (valueType == typeof(Guid))
        {
            return Guid.Parse(value);
        }
        if (valueType == typeof(DateTime))
        {
            return DateTime.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind
            );
        }
        if (valueType == typeof(DateTimeOffset))
        {
            return DateTimeOffset.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind
            );
        }
#if !NETSTANDARD
        if (valueType == typeof(DateOnly))
        {
            return DateOnly.Parse(value, CultureInfo.InvariantCulture);
        }
        if (valueType == typeof(TimeOnly))
        {
            return TimeOnly.Parse(value, CultureInfo.InvariantCulture);
        }
#endif
        if (valueType == typeof(TimeSpan))
        {
            return TimeSpan.Parse(value, CultureInfo.InvariantCulture);
        }
        if (valueType == typeof(Uri))
        {
            return new Uri(value, UriKind.RelativeOrAbsolute);
        }
        if (valueType == typeof(Version))
        {
            return Version.Parse(value);
        }
        if (valueType == typeof(byte[]))
        {
            return Convert.FromBase64String(value);
        }
        if (typeof(IConvertible).IsAssignableFrom(valueType))
        {
            return Convert.ChangeType(value, valueType, CultureInfo.InvariantCulture);
        }

        return ParseJson(value, valueType, null, string.Empty, string.Empty);
    }

    private object? ParseJson(
        string value,
        Type valueType,
        Exception? declinedBy,
        string propertyPath,
        string secretName
    )
    {
        if (valueType == typeof(object))
        {
            return value;
        }

        try
        {
            return JsonSerializer.Deserialize(value, valueType, _jsonOptions);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new FormatException(
                string.IsNullOrEmpty(propertyPath)
                    ? "The value is not valid JSON for the member type."
                    : $"The Key Vault secret '{secretName}' is not a valid value for member '{propertyPath}'.",
                declinedBy is null ? exception : new AggregateException(declinedBy, exception)
            );
        }
    }

    private string FormatValue(object? value, Type targetType)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var valueType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (valueType == typeof(string) && value is string text)
        {
            return text;
        }
        if (valueType == typeof(bool) && value is bool flag)
        {
            return flag ? "true" : "false";
        }
        if (valueType == typeof(byte[]) && value is byte[] bytes)
        {
            return Convert.ToBase64String(bytes);
        }
        if (value is IConvertible)
        {
            if (value is DateTime dateTime)
            {
                return dateTime.ToString("O", CultureInfo.InvariantCulture);
            }
            if (value is DateTimeOffset dateOffset)
            {
                return dateOffset.ToString("O", CultureInfo.InvariantCulture);
            }
            if (value is IFormattable formattable)
            {
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return JsonSerializer.Serialize(value, valueType, _jsonOptions);
    }

    private readonly record struct AppliedFragment(IConfiglueFragment Fragment, bool Matched);

    private sealed record SecretWrite(string SecretName, string Value);
}
