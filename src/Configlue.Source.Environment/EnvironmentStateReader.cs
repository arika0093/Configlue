using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Configlue;

namespace Configlue.Source.Environment;

/// <summary>Reads prefixed environment variables into a generated sparse model fragment.</summary>
/// <typeparam name="TFragment">The generated fragment type.</typeparam>
public sealed class EnvironmentStateReader<TFragment> : IStateReader<TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly ConfiglueModelSchema _schema;
    private readonly string _prefix;
    private readonly Func<IEnumerable<KeyValuePair<string, string?>>> _environmentVariables;
    private readonly Func<string, Type, object?> _valueParser;

    /// <summary>Creates an environment reader for a generated model schema.</summary>
    public EnvironmentStateReader(
        ConfiglueModelSchema schema,
        string prefix,
        Func<IEnumerable<KeyValuePair<string, string?>>>? environmentVariables = null,
        Func<string, Type, object?>? valueParser = null
    )
    {
        ArgumentNullException.ThrowIfNull(schema);
        _schema = schema;
        _prefix = NormalizePrefix(prefix) + "__";
        _environmentVariables = environmentVariables ?? ReadProcessEnvironmentVariables;
        _valueParser = valueParser ?? ParseScalar;
    }

    /// <inheritdoc />
    public ValueTask<StateReadResult<TFragment>> ReadAsync(
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mappings = GetEnvironmentMappings(_schema);
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _environmentVariables())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                !pair.Key.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)
                && !mappings.ContainsKey(pair.Key)
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

        var revision = CreateRevision(values, cancellationToken);
        if (values.Count == 0)
        {
            return ValueTask.FromResult(StateReadResult<TFragment>.NotFound(revision));
        }

        var assignments = new Dictionary<string, EnvironmentAssignment>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var pair in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (pair.Key.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
            {
                var path = pair.Key[_prefix.Length..];
                if (path.Length > 0)
                {
                    var segments = path.Split(["__"], StringSplitOptions.None);
                    if (segments.Any(string.IsNullOrWhiteSpace))
                    {
                        throw new FormatException(
                            $"Environment variable '{pair.Key}' contains an empty path segment."
                        );
                    }

                    if (ResolveCanonicalPath(_schema, segments, pair.Key) is { } canonicalPath)
                    {
                        targets.Add(canonicalPath);
                    }
                }
            }

            if (mappings.TryGetValue(pair.Key, out var explicitMappings))
            {
                foreach (var mapping in explicitMappings)
                {
                    targets.Add(mapping.PropertyPath);
                }
            }

            if (targets.Count > 1)
            {
                throw new FormatException(
                    $"Environment variable '{pair.Key}' maps to more than one model property."
                );
            }

            if (targets.Count == 0)
            {
                continue;
            }

            var target = targets.Single();
            if (
                !assignments.TryAdd(
                    target,
                    new EnvironmentAssignment(
                        target.Split('.', StringSplitOptions.None),
                        pair.Value,
                        pair.Key
                    )
                )
            )
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

        return ValueTask.FromResult(
            matchedAny
                ? StateReadResult<TFragment>.Success(
                    (TFragment)fragment,
                    revision,
                    _schema.ToMetadata()
                )
                : StateReadResult<TFragment>.NotFound(revision)
        );
    }

    private static IReadOnlyDictionary<
        string,
        IReadOnlyList<EnvironmentMapping>
    > GetEnvironmentMappings(ConfiglueModelSchema schema)
    {
        var mappings = new Dictionary<string, List<EnvironmentMapping>>(
            StringComparer.OrdinalIgnoreCase
        );
        CollectEnvironmentMappings(schema, [], new HashSet<Type>(), mappings);
        return mappings.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<EnvironmentMapping>)pair.Value,
            StringComparer.OrdinalIgnoreCase
        );
    }

    private static void CollectEnvironmentMappings(
        ConfiglueModelSchema schema,
        IReadOnlyList<string> parentPath,
        HashSet<Type> ancestors,
        Dictionary<string, List<EnvironmentMapping>> mappings
    )
    {
        if (!ancestors.Add(schema.ModelType))
        {
            return;
        }

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

                matches.Add(new EnvironmentMapping(string.Join('.', path)));
            }

            if (member.NestedSchemaFactory is not null)
            {
                CollectEnvironmentMappings(member.NestedSchemaFactory(), path, ancestors, mappings);
            }
        }

        ancestors.Remove(schema.ModelType);
    }

    private static string? ResolveCanonicalPath(
        ConfiglueModelSchema schema,
        IReadOnlyList<string> path,
        string environmentKey
    )
    {
        var canonicalPath = new List<string>(path.Count);
        for (var index = 0; index < path.Count; index++)
        {
            ConfiglueMemberSchema member = default;
            var found = false;
            foreach (var candidate in schema.Members)
            {
                if (!string.Equals(candidate.Name, path[index], StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (found)
                {
                    throw new FormatException(
                        $"Environment path segment '{path[index]}' is ambiguous in schema '{schema.Id}'."
                    );
                }

                member = candidate;
                found = true;
            }

            if (!found)
            {
                return null;
            }

            canonicalPath.Add(member.Name);
            if (index == path.Count - 1)
            {
                return string.Join('.', canonicalPath);
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
        if (normalized.Length == 0 || normalized.Contains(':', StringComparison.Ordinal))
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
        ConfiglueMemberSchema member = default;
        var found = false;
        foreach (var candidate in schema.Members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(candidate.Name, path[pathIndex], StringComparison.OrdinalIgnoreCase))
            {
                if (found)
                {
                    throw new FormatException(
                        $"Environment path segment '{path[pathIndex]}' is ambiguous in schema '{schema.Id}'."
                    );
                }

                member = candidate;
                found = true;
            }
        }

        if (!found)
        {
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
        SortedDictionary<string, string> values,
        CancellationToken cancellationToken
    )
    {
        var builder = new StringBuilder();
        foreach (var pair in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = pair.Key.ToUpperInvariant();
            builder
                .Append(key.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(key)
                .Append(pair.Value.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(pair.Value);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static object? ParseScalar(string value, Type targetType)
    {
        var nullableType = Nullable.GetUnderlyingType(targetType);
        var valueType = nullableType ?? targetType;
        if (nullableType is not null && value.Length == 0)
        {
            return null;
        }

        if (valueType == typeof(string))
            return value;
        if (valueType == typeof(bool))
            return bool.Parse(value);
        if (valueType == typeof(char))
        {
            return value.Length == 1
                ? value[0]
                : throw new FormatException(
                    "A character environment value must contain exactly one character."
                );
        }

        if (valueType.IsEnum)
            return Enum.Parse(valueType, value, ignoreCase: true);
        if (valueType == typeof(Guid))
            return Guid.Parse(value);
        if (valueType == typeof(DateTime))
            return DateTime.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind
            );
        if (valueType == typeof(DateTimeOffset))
            return DateTimeOffset.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind
            );
        if (valueType == typeof(DateOnly))
            return DateOnly.Parse(value, CultureInfo.InvariantCulture);
        if (valueType == typeof(TimeOnly))
            return TimeOnly.Parse(value, CultureInfo.InvariantCulture);
        if (valueType == typeof(TimeSpan))
            return TimeSpan.Parse(value, CultureInfo.InvariantCulture);
        if (valueType == typeof(Uri))
            return new Uri(value, UriKind.RelativeOrAbsolute);
        if (valueType == typeof(Version))
            return Version.Parse(value);
        if (valueType == typeof(byte[]))
            return Convert.FromBase64String(value);
        if (typeof(IConvertible).IsAssignableFrom(valueType))
        {
            return Convert.ChangeType(value, valueType, CultureInfo.InvariantCulture);
        }

        throw new NotSupportedException(
            $"Type '{targetType}' has no built-in environment conversion. Supply a value parser for this type."
        );
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

    private readonly record struct AppliedFragment(IConfiglueFragment Fragment, bool Matched);

    private sealed record EnvironmentMapping(string PropertyPath);

    private sealed record EnvironmentAssignment(string[] Path, string Value, string EnvironmentKey);
}
