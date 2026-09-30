using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Configlue;
using Configlue.Sources;

namespace Configlue.Source.Environment;

/// <summary>Reads prefixed environment variables into a generated sparse model fragment.</summary>
/// <typeparam name="TFragment">The generated fragment type.</typeparam>
public sealed class EnvironmentStateReader<TFragment> : ISourceReader<TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly ConfiglueModelSchema _schema;
    private readonly string _prefix;
    private readonly Func<IEnumerable<KeyValuePair<string, string?>>> _environmentVariables;
    private readonly Func<string, Type, object?> _valueParser;
    private readonly JsonSerializerOptions? _jsonOptions;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<EnvironmentMapping>> _mappings;
    private readonly IReadOnlyDictionary<ConfiglueModelSchema, SchemaMemberLookup> _lookups;

    /// <summary>Creates an environment reader for a generated model schema.</summary>
    public EnvironmentStateReader(
        ConfiglueModelSchema schema,
        string prefix,
        Func<IEnumerable<KeyValuePair<string, string?>>>? environmentVariables = null,
        Func<string, Type, object?>? valueParser = null,
        JsonSerializerOptions? jsonSerializerOptions = null
    )
    {
        ArgumentNullException.ThrowIfNull(schema);
        _schema = schema;
        _prefix = NormalizePrefix(prefix) + "__";
        _environmentVariables = environmentVariables ?? ReadProcessEnvironmentVariables;
        _valueParser = valueParser ?? ParseScalar;
        _jsonOptions = jsonSerializerOptions;
        var collectedMappings = new Dictionary<string, List<EnvironmentMapping>>(
            StringComparer.OrdinalIgnoreCase
        );
        var lookups = new Dictionary<ConfiglueModelSchema, SchemaMemberLookup>();
        CollectEnvironmentMappings(schema, [], new HashSet<Type>(), collectedMappings, lookups);
        _mappings = collectedMappings.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<EnvironmentMapping>)pair.Value,
            StringComparer.OrdinalIgnoreCase
        );
        _lookups = lookups;
    }

    /// <inheritdoc />
    public ValueTask<StateReadResult<TFragment>> ReadAsync(
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _environmentVariables())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                !pair.Key.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)
                && !_mappings.ContainsKey(pair.Key)
            )
            {
                continue;
            }

            if (!values.TryAdd(pair.Key, pair.Value ?? string.Empty))
            {
                throw new InvalidOperationException(
                    $"Environment variable '{pair.Key}' is listed more than once."
                );
            }
        }

        var keys = new List<string>(values.Keys);
        keys.Sort(StringComparer.OrdinalIgnoreCase);
        var revision = CreateRevision(values, keys, cancellationToken);
        if (values.Count == 0)
        {
            return new ValueTask<StateReadResult<TFragment>>(
                StateReadResult<TFragment>.NotFound(revision)
            );
        }

        var assignments = new Dictionary<string, EnvironmentAssignment>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = values[key];
            string[]? targetPath = null;
            string? target = null;
            if (key.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
            {
                var path = key[_prefix.Length..];
                if (path.Length > 0)
                {
                    var segments = path.Split(["__"], StringSplitOptions.None);
                    if (segments.Any(string.IsNullOrWhiteSpace))
                    {
                        throw new FormatException(
                            $"Environment variable '{key}' contains an empty path segment."
                        );
                    }

                    targetPath = ResolveCanonicalPath(_schema, segments, key);
                    if (targetPath is not null)
                    {
                        target = string.Join(".", targetPath);
                    }
                }
            }

            if (_mappings.TryGetValue(key, out var explicitMappings))
            {
                foreach (var mapping in explicitMappings)
                {
                    if (target is null)
                    {
                        target = mapping.PropertyPath;
                        targetPath = mapping.PropertyPathSegments;
                    }
                    else if (
                        !string.Equals(
                            target,
                            mapping.PropertyPath,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        throw new FormatException(
                            $"Environment variable '{key}' maps to more than one model property."
                        );
                    }
                }
            }

            if (target is null || targetPath is null)
            {
                continue;
            }

            if (!assignments.TryAdd(target, new EnvironmentAssignment(targetPath, value, key)))
            {
                throw new InvalidOperationException(
                    $"More than one environment variable maps to model property '{target}'."
                );
            }
        }

        IConfiglueFragment fragment = _schema.CreateEmptyFragment();
        var matchedAny = false;
        foreach (var assignment in assignments.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var applied = SetValue(
                fragment,
                _schema,
                assignment.Path,
                0,
                assignment.Value,
                assignment.EnvironmentKey,
                cancellationToken
            );
            fragment = applied.Fragment;
            matchedAny |= applied.Matched;
        }

        return new ValueTask<StateReadResult<TFragment>>(
            matchedAny
                ? StateReadResult<TFragment>.Success(
                    (TFragment)fragment,
                    revision,
                    _schema.ToMetadata()
                )
                : StateReadResult<TFragment>.NotFound(revision)
        );
    }

    private static void CollectEnvironmentMappings(
        ConfiglueModelSchema schema,
        IReadOnlyList<string> parentPath,
        HashSet<Type> ancestors,
        Dictionary<string, List<EnvironmentMapping>> mappings,
        Dictionary<ConfiglueModelSchema, SchemaMemberLookup> lookups
    )
    {
        if (!ancestors.Add(schema.ModelType))
        {
            return;
        }

        lookups[schema] = SchemaMemberLookup.Create(schema);
        foreach (var member in schema.Members)
        {
            var path = parentPath.Append(member.Name).ToArray();
            if (member.EnvironmentVariableName is { } environmentName)
            {
                if (!mappings.TryGetValue(environmentName, out var matches))
                {
                    matches = [];
                    mappings.Add(environmentName, matches);
                }

                matches.Add(new EnvironmentMapping(string.Join(".", path), path));
            }

            if (member.NestedSchemaFactory is not null)
            {
                CollectEnvironmentMappings(
                    member.NestedSchemaFactory(),
                    path,
                    ancestors,
                    mappings,
                    lookups
                );
            }
        }

        ancestors.Remove(schema.ModelType);
    }

    private string[]? ResolveCanonicalPath(
        ConfiglueModelSchema schema,
        IReadOnlyList<string> path,
        string environmentKey
    )
    {
        var canonicalPath = new string[path.Count];
        for (var index = 0; index < path.Count; index++)
        {
            if (!_lookups[schema].TryResolve(path[index], out var member, out var isAmbiguous))
            {
                if (isAmbiguous)
                {
                    throw new FormatException(
                        $"Environment path segment '{path[index]}' is ambiguous in schema '{schema.Id}'."
                    );
                }

                return null;
            }

            canonicalPath[index] = member.Name;
            if (index == path.Count - 1)
            {
                return canonicalPath;
            }

            if (member.NestedSchemaFactory is null)
            {
                throw new FormatException(
                    $"Environment variable '{environmentKey}' continues past non-nested member '{member.Name}'."
                );
            }

            schema = member.NestedSchemaFactory();
        }

        return null;
    }

    internal static string NormalizePrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var normalized = prefix.TrimEnd('_');
        if (normalized.Length == 0 || normalized.IndexOf(':') >= 0)
        {
            throw new ArgumentException(
                "The environment prefix must contain a name and cannot contain ':'.",
                nameof(prefix)
            );
        }

        return normalized;
    }

    private AppliedFragment SetValue(
        IConfiglueFragment fragment,
        ConfiglueModelSchema schema,
        string[] path,
        int pathIndex,
        string value,
        string environmentKey,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_lookups[schema].TryResolve(path[pathIndex], out var member, out var isAmbiguous))
        {
            if (isAmbiguous)
            {
                throw new FormatException(
                    $"Environment path segment '{path[pathIndex]}' is ambiguous in schema '{schema.Id}'."
                );
            }

            return new AppliedFragment(fragment, false);
        }

        if (pathIndex == path.Length - 1)
        {
            if (member.NestedSchemaFactory is not null)
            {
                throw new FormatException(
                    $"Environment variable '{environmentKey}' names a nested model. Use additional '__' segments to set its members."
                );
            }

            var parsed = ParseValue(value, member.ValueType, member.Name, environmentKey);
            return new AppliedFragment(fragment.WithMember(member.Id, parsed), true);
        }

        if (member.NestedSchemaFactory is null)
        {
            throw new FormatException(
                $"Environment variable '{environmentKey}' continues past non-nested member '{member.Name}'."
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
            environmentKey,
            cancellationToken
        );
        return nestedResult.Matched
            ? new AppliedFragment(fragment.WithMember(member.Id, nestedResult.Fragment), true)
            : new AppliedFragment(fragment, false);
    }

    private object? ParseValue(
        string value,
        Type targetType,
        string memberName,
        string environmentKey
    )
    {
        object? parsed;
        try
        {
            parsed = _valueParser(value, targetType);
        }
        catch (NotSupportedException exception)
        {
            // A custom parser may decline types it does not handle. Fall back to JSON so
            // collection and object members work without a custom parser for every type.
            parsed = ParseJson(value, targetType, exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new FormatException(
                $"Environment variable '{environmentKey}' is not a valid value for member '{memberName}' of type '{targetType}'.",
                exception
            );
        }

        var valueType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (parsed is null)
        {
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null)
            {
                throw new FormatException(
                    $"The parser returned null for non-nullable member '{memberName}'."
                );
            }

            return null;
        }

        if (!valueType.IsInstanceOfType(parsed))
        {
            throw new FormatException(
                $"The parser returned '{parsed.GetType()}' for member '{memberName}', which requires '{targetType}'."
            );
        }

        return parsed;
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

    private static string CreateRevision(
        IReadOnlyDictionary<string, string> values,
        List<string> keys,
        CancellationToken cancellationToken
    )
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var pair in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendHashedString(hash, pair.ToUpperInvariant());
            AppendHashedString(hash, values[pair]);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendHashedString(IncrementalHash hash, string value)
    {
        Span<byte> prefix = stackalloc byte[12];
#if NETSTANDARD2_0
        var prefixLength = WriteLengthPrefix(prefix, value.Length);
        hash.AppendData(prefix[..prefixLength].ToArray());
        hash.AppendData(Encoding.UTF8.GetBytes(value));
#else
        var prefixLength = WriteLengthPrefix(prefix, value.Length);
        hash.AppendData(prefix[..prefixLength]);
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount == 0)
        {
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(value, buffer);
            hash.AppendData(buffer.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
#endif
    }

    private static int WriteLengthPrefix(Span<byte> buffer, int length)
    {
        var digits = 0;
        var remaining = length;
        do
        {
            digits++;
            remaining /= 10;
        } while (remaining != 0);

        var index = digits;
        remaining = length;
        do
        {
            index--;
            buffer[index] = (byte)('0' + (remaining % 10));
            remaining /= 10;
        } while (remaining != 0);

        buffer[digits] = (byte)':';
        return digits + 1;
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
                    "A character environment value must contain exactly one character."
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
#if !NETSTANDARD2_0
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

        return ParseJson(value, valueType, null);
    }

    /// <summary>Interprets an environment value as JSON for types without a scalar conversion.</summary>
    /// <remarks>Collections and objects use JSON so values like <c>["nord","dracula"]</c> bind directly.</remarks>
    private object? ParseJson(string value, Type valueType, Exception? declinedBy)
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
                $"The value is not valid JSON for type '{valueType}'. Supply a value parser for this type.",
                declinedBy is null ? exception : new AggregateException(declinedBy, exception)
            );
        }
    }

    private static IEnumerable<KeyValuePair<string, string?>> ReadProcessEnvironmentVariables()
    {
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                yield return new KeyValuePair<string, string?>(key, entry.Value?.ToString());
            }
        }
    }

    private readonly record struct AppliedFragment
    {
        public IConfiglueFragment Fragment { get; init; }
        public bool Matched { get; init; }

        public AppliedFragment(IConfiglueFragment Fragment, bool Matched)
        {
            this.Fragment = Fragment;
            this.Matched = Matched;
        }

        public void Deconstruct(out IConfiglueFragment Fragment, out bool Matched)
        {
            Fragment = this.Fragment;
            Matched = this.Matched;
        }
    }

    private sealed record EnvironmentMapping
    {
        public string PropertyPath { get; init; }
        public string[] PropertyPathSegments { get; init; }

        public EnvironmentMapping(string PropertyPath, string[] PropertyPathSegments)
        {
            this.PropertyPath = PropertyPath;
            this.PropertyPathSegments = PropertyPathSegments;
        }

        public void Deconstruct(out string PropertyPath, out string[] PropertyPathSegments)
        {
            PropertyPath = this.PropertyPath;
            PropertyPathSegments = this.PropertyPathSegments;
        }
    }

    private sealed record EnvironmentAssignment
    {
        public string[] Path { get; init; }
        public string Value { get; init; }
        public string EnvironmentKey { get; init; }

        public EnvironmentAssignment(string[] Path, string Value, string EnvironmentKey)
        {
            this.Path = Path;
            this.Value = Value;
            this.EnvironmentKey = EnvironmentKey;
        }

        public void Deconstruct(out string[] Path, out string Value, out string EnvironmentKey)
        {
            Path = this.Path;
            Value = this.Value;
            EnvironmentKey = this.EnvironmentKey;
        }
    }
}
