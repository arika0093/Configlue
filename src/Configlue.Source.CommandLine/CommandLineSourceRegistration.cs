using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Configlue;
using Configlue.Source.Environment;

namespace Configlue.Source.CommandLine;

/// <summary>Options for a sparse read-only source based on an already parsed command line.</summary>
public sealed class CommandLineSourceOptions
{
    /// <summary>Stable logical source ID.</summary>
    public required string Id { get; init; }

    /// <summary>The application's existing parse result.</summary>
    public required ParseResult ParseResult { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>JSON options used to preserve the types already parsed by System.CommandLine.</summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; init; }
}

/// <summary>Builds explicit mappings from command-line symbols to generated model member paths.</summary>
public sealed class CommandLineMappingBuilder
{
    private readonly List<Mapping> _mappings = [];

    /// <summary>Maps an option to a dotted model property path.</summary>
    public void Map<TValue>(Option<TValue> option, string propertyPath)
    {
        ArgumentNullException.ThrowIfNull(option);
        Add(
            option,
            propertyPath,
            result => result.GetValue(option),
            result => result.GetResult(option)
        );
    }

    /// <summary>Maps an option to a generated model property selector.</summary>
    public void Map<TModel, TValue>(
        Option<TValue> option,
        Expression<Func<TModel, TValue>> property
    )
    {
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(property);
        Map(option, GetPropertyPath(property));
    }

    /// <summary>Maps an argument to a dotted model property path.</summary>
    public void Map<TValue>(Argument<TValue> argument, string propertyPath)
    {
        ArgumentNullException.ThrowIfNull(argument);
        Add(
            argument,
            propertyPath,
            result => result.GetValue(argument),
            result => result.GetResult(argument)
        );
    }

    /// <summary>Maps an argument to a generated model property selector.</summary>
    public void Map<TModel, TValue>(
        Argument<TValue> argument,
        Expression<Func<TModel, TValue>> property
    )
    {
        ArgumentNullException.ThrowIfNull(argument);
        ArgumentNullException.ThrowIfNull(property);
        Map(argument, GetPropertyPath(property));
    }

    internal IReadOnlyList<Mapping> Mappings => _mappings;

    private static string GetPropertyPath<TModel, TValue>(Expression<Func<TModel, TValue>> selector)
    {
        Expression expression = selector.Body;
        while (
            expression
                is UnaryExpression
                {
                    NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked
                } conversion
        )
        {
            expression = conversion.Operand;
        }

        var members = new Stack<string>();
        while (expression is MemberExpression memberExpression)
        {
            if (memberExpression.Member is not PropertyInfo)
            {
                throw new ArgumentException(
                    "A command-line mapping must select model properties.",
                    nameof(selector)
                );
            }

            members.Push(memberExpression.Member.Name);
            expression = memberExpression.Expression!;
        }

        if (expression != selector.Parameters[0] || members.Count == 0)
        {
            throw new ArgumentException(
                "A command-line mapping must be a direct or nested model property selector.",
                nameof(selector)
            );
        }

        return string.Join('.', members);
    }

    private void Add(
        Symbol symbol,
        string path,
        Func<ParseResult, object?> getValue,
        Func<ParseResult, SymbolResult?> getResult
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var segments = path.Split('.', StringSplitOptions.None);
        if (segments.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "A model path cannot contain empty segments.",
                nameof(path)
            );
        }
        if (_mappings.Any(mapping => ReferenceEquals(mapping.Symbol, symbol)))
        {
            throw new ArgumentException(
                $"Command-line symbol '{symbol.Name}' is mapped more than once.",
                nameof(symbol)
            );
        }
        if (
            _mappings.Any(mapping =>
                string.Equals(mapping.PropertyPath, path, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            throw new ArgumentException(
                $"Model path '{path}' is mapped more than once.",
                nameof(path)
            );
        }
        _mappings.Add(new Mapping(symbol, path, getValue, getResult));
    }

    internal sealed record Mapping(
        Symbol Symbol,
        string PropertyPath,
        Func<ParseResult, object?> GetValue,
        Func<ParseResult, SymbolResult?> GetResult
    );
}

/// <summary>Registers sparse sources from an existing System.CommandLine parse result.</summary>
public static class CommandLineSourceRegistration
{
    /// <summary>Adds a read-only command-line source using explicit symbol-to-member mappings.</summary>
    public static void FromCommandLine(
        this ConfiglueSourceSetBuilder sources,
        CommandLineSourceOptions options,
        Action<CommandLineMappingBuilder> configureMappings
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configureMappings);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Id);
        ArgumentNullException.ThrowIfNull(options.ParseResult);
        var mappings = new CommandLineMappingBuilder();
        configureMappings(mappings);
        if (mappings.Mappings.Count == 0)
        {
            throw new ArgumentException(
                "At least one command-line symbol mapping is required.",
                nameof(configureMappings)
            );
        }
        sources.Add(new Definition(options, mappings.Mappings.ToArray()));
    }

    private sealed class Definition(
        CommandLineSourceOptions options,
        IReadOnlyList<CommandLineMappingBuilder.Mapping> mappings
    ) : IConfiglueSourceDefinition
    {
        public StateSource<TFragment> Create<TFragment>(
            ConfiglueModelSchema modelSchema,
            IServiceProvider? serviceProvider,
            Action<IDisposable> ownResource
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ValidateMappings(modelSchema, mappings.Select(mapping => mapping.PropertyPath));
            var serializerOptions = options.JsonSerializerOptions;
            var reader = new EnvironmentStateReader<TFragment>(
                modelSchema,
                "CONFIGLUE_COMMAND_LINE",
                GetVariables,
                (value, targetType) =>
                    JsonSerializer.Deserialize(value, targetType, serializerOptions)
            );
            return new StateSource<TFragment>(
                options.Id,
                reader,
                options.Priority,
                options.FallbackCondition,
                physicalOrigin: "command-line:parse-result"
            );

            IEnumerable<KeyValuePair<string, string?>> GetVariables()
            {
                if (options.ParseResult.Errors.Count > 0)
                {
                    throw new FormatException(
                        "The command-line parse result contains errors: "
                            + string.Join(
                                "; ",
                                options.ParseResult.Errors.Select(error => error.Message)
                            )
                    );
                }
                if (options.ParseResult.UnmatchedTokens.Count > 0)
                {
                    throw new FormatException(
                        "The command line contains unmatched tokens: "
                            + string.Join(" ", options.ParseResult.UnmatchedTokens)
                    );
                }
                var values = new List<KeyValuePair<string, string?>>();
                foreach (var mapping in mappings)
                {
                    var result = mapping.GetResult(options.ParseResult);
                    if (
                        result is null
                        || result.Tokens.Count == 0
                        || result is OptionResult { Implicit: true }
                        || result is ArgumentResult { Implicit: true }
                    )
                        continue;
                    var value = mapping.GetValue(options.ParseResult);
                    var json = JsonSerializer.Serialize(
                        value,
                        value?.GetType() ?? typeof(object),
                        serializerOptions
                    );
                    var key =
                        "CONFIGLUE_COMMAND_LINE__"
                        + mapping.PropertyPath.Replace(".", "__", StringComparison.Ordinal);
                    values.Add(new KeyValuePair<string, string?>(key, json));
                }
                return values;
            }
        }

        private static void ValidateMappings(
            ConfiglueModelSchema schema,
            IEnumerable<string> mappingPaths
        )
        {
            foreach (var propertyPath in mappingPaths)
            {
                var current = schema;
                var parts = propertyPath.Split('.');
                for (var index = 0; index < parts.Length; index++)
                {
                    var matches = current
                        .Members.Where(member =>
                            string.Equals(
                                member.Name,
                                parts[index],
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        .ToArray();
                    if (matches.Length != 1)
                    {
                        throw new ArgumentException(
                            $"Command-line mapping path '{propertyPath}' has an unknown or ambiguous member '{parts[index]}'."
                        );
                    }
                    var member = matches[0];
                    var isFinal = index == parts.Length - 1;
                    if (isFinal && member.NestedSchemaFactory is not null)
                    {
                        throw new ArgumentException(
                            $"Command-line mapping path '{propertyPath}' must target a leaf member."
                        );
                    }
                    if (!isFinal)
                    {
                        current =
                            member.NestedSchemaFactory?.Invoke()
                            ?? throw new ArgumentException(
                                $"Command-line mapping path '{propertyPath}' continues past scalar member '{member.Name}'."
                            );
                    }
                }
            }
        }
    }
}
