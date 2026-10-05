using Configlue;
using Configlue.CompilerServices;
using Configlue.Resources;
using Configlue.Sources;

namespace Configlue.Source.CommandLine;

/// <summary>Reads mapped command-line values into a generated sparse model fragment.</summary>
/// <typeparam name="TFragment">The generated fragment type.</typeparam>
internal sealed class CommandLineStateReader<TFragment> : ISourceReader<TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly ConfiglueModelSchema _schema;
    private readonly CommandLineSourceOptions _options;
    private readonly IReadOnlyList<CommandLineMappingBuilder.Mapping> _mappings;
    private readonly object _cacheLock = new();
    private StateReadResult<TFragment> _cachedResult;
    private volatile bool _hasCachedResult;

    public CommandLineStateReader(
        ConfiglueModelSchema schema,
        CommandLineSourceOptions options,
        IReadOnlyList<CommandLineMappingBuilder.Mapping> mappings
    )
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mappings);
        _schema = schema;
        _options = options;
        _mappings = mappings;
    }

    public ValueTask<StateReadResult<TFragment>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        _ = context;
        cancellationToken.ThrowIfCancellationRequested();
        if (!_hasCachedResult)
        {
            lock (_cacheLock)
            {
                if (!_hasCachedResult)
                {
                    _cachedResult = ReadCore(cancellationToken);
                    _hasCachedResult = true;
                }
            }
        }

        return new ValueTask<StateReadResult<TFragment>>(_cachedResult);
    }

    private StateReadResult<TFragment> ReadCore(CancellationToken cancellationToken)
    {
        var parseResult = _options.ParseResult;
        if (parseResult.Errors.Count > 0)
        {
            throw new FormatException(
                "The command-line parse result contains errors: "
                    + string.Join("; ", parseResult.Errors.Select(error => error.Message))
            );
        }

        if (parseResult.UnmatchedTokens.Count > 0)
        {
            throw new FormatException(
                "The command line contains unmatched tokens: "
                    + string.Join(" ", parseResult.UnmatchedTokens)
            );
        }

        // CommandLine owns symbol/value extraction only: collect present symbols,
        // then delegate path resolution, conversion, conflict handling, and
        // revision to the shared text-assignment binder. Later mappings win.
        var assignments = new List<TextAssignment>(_mappings.Count);
        foreach (var mapping in _mappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = mapping.Resolve(parseResult);
            if (resolved is not { } value)
            {
                continue;
            }

            assignments.Add(
                new TextAssignment(mapping.PropertyPathSegments, value.Value, mapping.Symbol.Name)
            );
        }

        var bound = TextAssignmentBinder.Bind(
            _schema,
            assignments,
            new TextAssignmentBinderOptions
            {
                DuplicatePolicy = TextAssignmentDuplicatePolicy.LastWins,
            },
            cancellationToken
        );

        if (!bound.MatchedAny)
        {
            return StateReadResult<TFragment>.NotFound(bound.Revision);
        }

        return StateReadResult<TFragment>.Success(
            (TFragment)bound.Fragment,
            bound.Revision,
            _schema.ToMetadata()
        );
    }
}
