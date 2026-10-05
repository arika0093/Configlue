using System.Collections;
using System.Text.Json;
using Configlue;
using Configlue.CompilerServices;
using Configlue.Resources;
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
    private readonly Func<string, Type, object?>? _valueParser;
    private readonly JsonSerializerOptions? _jsonOptions;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<EnvironmentMapping>> _mappings;
    private readonly IReadOnlyDictionary<ConfiglueModelSchema, TextAssignmentMemberLookup> _lookups;

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
        _valueParser = valueParser;
        _jsonOptions = jsonSerializerOptions;
        var collectedMappings = new Dictionary<string, List<EnvironmentMapping>>(
            StringComparer.OrdinalIgnoreCase
        );
        var lookups = new Dictionary<ConfiglueModelSchema, TextAssignmentMemberLookup>();
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
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        _ = context;
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

        // Environment owns naming/prefix rules only: normalize each variable to a
        // member path (prefix split or explicit mapping), then delegate binding,
        // conversion, and revision to the shared text-assignment binder.
        var matchedTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var binderAssignments = new List<TextAssignment>(values.Count);
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = values[key];
            string[]? targetPath = null;
            string? target = null;
            string[]? rawSegments = null;
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

                    rawSegments = segments;
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
                        rawSegments ??= mapping.PropertyPathSegments;
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
                binderAssignments.Add(new TextAssignment(rawSegments ?? [key], value, key));
                continue;
            }

            if (!matchedTargets.TryAdd(target, key))
            {
                throw new InvalidOperationException(
                    $"More than one environment variable maps to model property '{target}'."
                );
            }

            binderAssignments.Add(new TextAssignment(targetPath, value, key));
        }

        var bound = TextAssignmentBinder.Bind(
            _schema,
            binderAssignments,
            new TextAssignmentBinderOptions
            {
                TextParser = _valueParser,
                JsonOptions = _jsonOptions,
                DuplicatePolicy = TextAssignmentDuplicatePolicy.Throw,
            },
            cancellationToken
        );

        if (!bound.MatchedAny)
        {
            return new ValueTask<StateReadResult<TFragment>>(
                StateReadResult<TFragment>.NotFound(bound.Revision)
            );
        }

        return new ValueTask<StateReadResult<TFragment>>(
            StateReadResult<TFragment>.Success(
                (TFragment)bound.Fragment,
                bound.Revision,
                _schema.ToMetadata()
            )
        );
    }

    private static void CollectEnvironmentMappings(
        ConfiglueModelSchema schema,
        IReadOnlyList<string> parentPath,
        HashSet<Type> ancestors,
        Dictionary<string, List<EnvironmentMapping>> mappings,
        Dictionary<ConfiglueModelSchema, TextAssignmentMemberLookup> lookups
    )
    {
        if (!ancestors.Add(schema.ModelType))
        {
            return;
        }

        lookups[schema] = TextAssignmentMemberLookup.Create(schema);
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
}
