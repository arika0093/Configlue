using System.Collections;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using Configlue;
using Configlue.CompilerServices;

namespace Configlue.Source.CommandLine;

/// <summary>Options for a sparse read-only source based on an already parsed command line.</summary>
public sealed class CommandLineSourceOptions
{
    /// <summary>Stable logical source ID.</summary>
    public required string Id { get; init; }

    /// <summary>The application's existing parse result. System.CommandLine v2 and v3 results are supported.</summary>
    public required ParseResult ParseResult { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;
}

/// <summary>Builds explicit mappings from command-line symbols to generated model member paths.</summary>
/// <remarks>
/// One symbol may fan out to several members, and several symbols may target one member; when
/// several present symbols target the same member, the last mapping wins.
/// </remarks>
public sealed class CommandLineMappingBuilder
{
    private readonly List<Mapping> _mappings = [];

    /// <summary>Maps an option to a dotted model property path.</summary>
    public void Map<TValue>(Option<TValue> option, string propertyPath)
    {
        ArgumentNullException.ThrowIfNull(option);
        Add(option, propertyPath, result => ResolveDirect(result, option));
    }

    /// <summary>Maps an option to a generated model property selector.</summary>
    public void Map<TModel, TValue>(
        Option<TValue> option,
        Expression<Func<TModel, TValue>> property
    )
    {
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(property);
        Map(
            option,
            ConfiglueMemberSelector.GetPropertyPath(
                property,
                "command-line mapping",
                nameof(property)
            )
        );
    }

    /// <summary>Maps an option to a dotted model property path with a value conversion.</summary>
    public void Map<TValue, TMember>(
        Option<TValue> option,
        string propertyPath,
        Func<TValue?, TMember?> convert
    )
    {
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(convert);
        Add(
            option,
            propertyPath,
            result => ResolveConverted(result, option, propertyPath, convert)
        );
    }

    /// <summary>Maps an option to a generated model property selector with a value conversion.</summary>
    public void Map<TModel, TValue, TMember>(
        Option<TValue> option,
        Expression<Func<TModel, TMember>> property,
        Func<TValue?, TMember?> convert
    )
    {
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(convert);
        Map(
            option,
            ConfiglueMemberSelector.GetPropertyPath(
                property,
                "command-line mapping",
                nameof(property)
            ),
            convert
        );
    }

    /// <summary>Maps an argument to a dotted model property path.</summary>
    public void Map<TValue>(Argument<TValue> argument, string propertyPath)
    {
        ArgumentNullException.ThrowIfNull(argument);
        Add(argument, propertyPath, result => ResolveDirect(result, argument));
    }

    /// <summary>Maps an argument to a generated model property selector.</summary>
    public void Map<TModel, TValue>(
        Argument<TValue> argument,
        Expression<Func<TModel, TValue>> property
    )
    {
        ArgumentNullException.ThrowIfNull(argument);
        ArgumentNullException.ThrowIfNull(property);
        Map(
            argument,
            ConfiglueMemberSelector.GetPropertyPath(
                property,
                "command-line mapping",
                nameof(property)
            )
        );
    }

    /// <summary>Maps an argument to a dotted model property path with a value conversion.</summary>
    public void Map<TValue, TMember>(
        Argument<TValue> argument,
        string propertyPath,
        Func<TValue?, TMember?> convert
    )
    {
        ArgumentNullException.ThrowIfNull(argument);
        ArgumentNullException.ThrowIfNull(convert);
        Add(
            argument,
            propertyPath,
            result => ResolveConverted(result, argument, propertyPath, convert)
        );
    }

    /// <summary>Maps an argument to a generated model property selector with a value conversion.</summary>
    public void Map<TModel, TValue, TMember>(
        Argument<TValue> argument,
        Expression<Func<TModel, TMember>> property,
        Func<TValue?, TMember?> convert
    )
    {
        ArgumentNullException.ThrowIfNull(argument);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(convert);
        Map(
            argument,
            ConfiglueMemberSelector.GetPropertyPath(
                property,
                "command-line mapping",
                nameof(property)
            ),
            convert
        );
    }

    internal IReadOnlyList<Mapping> Mappings => _mappings;

    internal static bool IsPresent(SymbolResult? result) =>
        result is not null
        && result.Tokens.Count != 0
        && result is not OptionResult { Implicit: true }
        && result is not ArgumentResult { Implicit: true };

    private static MappingValue? ResolveDirect<TValue>(
        ParseResult parseResult,
        Option<TValue> option
    )
    {
        if (!IsPresent(parseResult.GetResult(option)))
        {
            return null;
        }

        return new MappingValue(parseResult.GetValue(option));
    }

    private static MappingValue? ResolveDirect<TValue>(
        ParseResult parseResult,
        Argument<TValue> argument
    )
    {
        if (!IsPresent(parseResult.GetResult(argument)))
        {
            return null;
        }

        return new MappingValue(parseResult.GetValue(argument));
    }

    private static MappingValue? ResolveConverted<TValue, TMember>(
        ParseResult parseResult,
        Option<TValue> option,
        string propertyPath,
        Func<TValue?, TMember?> convert
    )
    {
        if (!IsPresent(parseResult.GetResult(option)))
        {
            return null;
        }

        try
        {
            return new MappingValue(convert(parseResult.GetValue(option)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new FormatException(
                $"The command-line option '{option.Name}' could not be converted for model path '{propertyPath}'.",
                exception
            );
        }
    }

    private static MappingValue? ResolveConverted<TValue, TMember>(
        ParseResult parseResult,
        Argument<TValue> argument,
        string propertyPath,
        Func<TValue?, TMember?> convert
    )
    {
        if (!IsPresent(parseResult.GetResult(argument)))
        {
            return null;
        }

        try
        {
            return new MappingValue(convert(parseResult.GetValue(argument)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new FormatException(
                $"The command-line argument '{argument.Name}' could not be converted for model path '{propertyPath}'.",
                exception
            );
        }
    }

    private void Add(Symbol symbol, string path, Func<ParseResult, MappingValue?> resolve)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var segments = path.Split(new[] { '.' }, StringSplitOptions.None);
        if (segments.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "A model path cannot contain empty segments.",
                nameof(path)
            );
        }

        if (
            _mappings.Any(mapping =>
                ReferenceEquals(mapping.Symbol, symbol)
                && string.Equals(mapping.PropertyPath, path, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            throw new ArgumentException(
                $"Command-line symbol '{symbol.Name}' is already mapped to model path '{path}'.",
                nameof(symbol)
            );
        }

        _mappings.Add(new Mapping(symbol, path, segments, resolve));
    }

    internal readonly record struct MappingValue
    {
        public object? Value { get; init; }

        public MappingValue(object? Value)
        {
            this.Value = Value;
        }

        public void Deconstruct(out object? Value)
        {
            Value = this.Value;
        }
    }

    internal sealed record Mapping
    {
        public Symbol Symbol { get; init; }
        public string PropertyPath { get; init; }
        public string[] PropertyPathSegments { get; init; }
        public Func<ParseResult, MappingValue?> Resolve { get; init; }

        public Mapping(
            Symbol Symbol,
            string PropertyPath,
            string[] PropertyPathSegments,
            Func<ParseResult, MappingValue?> Resolve
        )
        {
            this.Symbol = Symbol;
            this.PropertyPath = PropertyPath;
            this.PropertyPathSegments = PropertyPathSegments;
            this.Resolve = Resolve;
        }

        public void Deconstruct(
            out Symbol Symbol,
            out string PropertyPath,
            out string[] PropertyPathSegments,
            out Func<ParseResult, MappingValue?> Resolve
        )
        {
            Symbol = this.Symbol;
            PropertyPath = this.PropertyPath;
            PropertyPathSegments = this.PropertyPathSegments;
            Resolve = this.Resolve;
        }
    }
}

/// <summary>Registers sparse sources from an existing System.CommandLine parse result.</summary>
public static class CommandLineSourceRegistration
{
    /// <summary>Begins a fluent command-line registration with the supplied mappings.</summary>
    public static CommandLineSourceBuilder CommandLine(
        this ConfiglueSourceSetBuilder sources,
        ParseResult parseResult,
        Action<CommandLineMappingBuilder> configureMappings,
        string id = "command-line"
    )
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(configureMappings);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var registration = sources.FromCommandLine(
            new CommandLineSourceOptions { Id = id, ParseResult = parseResult },
            configureMappings
        );
        return new CommandLineSourceBuilder(registration);
    }

    /// <summary>Adds a read-only command-line source using explicit symbol-to-member mappings.</summary>
    public static ConfiglueSourceRegistration FromCommandLine(
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

        return ((IConfiglueSourceRegistrationSink)sources).Add(
            new Definition(options, mappings.Mappings.ToArray())
        );
    }

    private sealed class Definition(
        CommandLineSourceOptions options,
        IReadOnlyList<CommandLineMappingBuilder.Mapping> mappings
    ) : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            return context.Complete(CreateSourceCore<TFragment>(context.ModelSchema));
        }

        private StateSource<TFragment> CreateSourceCore<TFragment>(ConfiglueModelSchema modelSchema)
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ValidateMappings(modelSchema, mappings.Select(mapping => mapping.PropertyPath));
            var reader = new CommandLineStateReader<TFragment>(modelSchema, options, mappings);
            return new StateSource<TFragment>(
                options.Id,
                reader,
                new StateSourceOptions<TFragment>
                {
                    Priority = options.Priority,
                    FallbackCondition = options.FallbackCondition,
                    PhysicalOrigin = "command-line:parse-result",
                }
            );
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

/// <summary>Configures common routing behavior for a registered command-line source.</summary>
public sealed class CommandLineSourceBuilder
{
    private readonly ConfiglueSourceRegistration _registration;

    internal CommandLineSourceBuilder(ConfiglueSourceRegistration registration)
    {
        _registration = registration;
    }

    /// <summary>Sets the read priority for this command-line source.</summary>
    public CommandLineSourceBuilder Priority(int priority)
    {
        _registration.Priority(priority);
        return this;
    }

    /// <summary>Sets the statuses that allow resolution to fall back to lower-priority sources.</summary>
    public CommandLineSourceBuilder FallbackWhen(StateFallbackCondition condition)
    {
        _registration.FallbackWhen(condition);
        return this;
    }
}
