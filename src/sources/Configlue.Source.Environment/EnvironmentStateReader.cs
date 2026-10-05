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
        CollectEnvironmentMappings(schema, [], new HashSet<Type>(), collectedMappings);
        _mappings = collectedMappings.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<EnvironmentMapping>)pair.Value,
            StringComparer.OrdinalIgnoreCase
        );
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

        // Environment owns naming only: normalize each variable to raw member-name
        // segments (prefix split or explicit mapping) and delegate canonical
        // resolution, conversion, duplicate handling, and revision to the shared
        // text-assignment binder. Single-key conflicts (one variable naming two
        // members) are a naming error detected here by case-insensitive path
        // comparison; multi-key duplicates are the binder's Throw policy.
        var binderAssignments = new List<TextAssignment>(values.Count);
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = values[key];
            string[]? prefixSegments = null;
            string? prefixPath = null;
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

                    prefixSegments = segments;
                    prefixPath = string.Join(".", segments);
                }
            }

            string[]? chosenSegments = prefixSegments;
            string? chosenPath = prefixPath;
            if (_mappings.TryGetValue(key, out var explicitMappings))
            {
                foreach (var mapping in explicitMappings)
                {
                    if (chosenPath is null)
                    {
                        chosenPath = mapping.PropertyPath;
                        chosenSegments = mapping.PropertyPathSegments;
                    }
                    else if (
                        !string.Equals(
                            chosenPath,
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

            binderAssignments.Add(
                chosenSegments is null
                    ? new TextAssignment([key], value, key)
                    : new TextAssignment(chosenSegments, value, key)
            );
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

                matches.Add(new EnvironmentMapping(string.Join(".", path), path));
            }

            if (member.NestedSchemaFactory is not null)
            {
                CollectEnvironmentMappings(member.NestedSchemaFactory(), path, ancestors, mappings);
            }
        }

        ancestors.Remove(schema.ModelType);
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
