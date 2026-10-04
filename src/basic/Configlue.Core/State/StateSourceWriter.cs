using Configlue.Resources;
using Configlue.Sources;

namespace Configlue.State;

/// <summary>Routes writes independently from read-source selection using deterministic ownership.</summary>
public sealed class StateSourceWriter<T> : ISourceWriter<T>
{
    private readonly StateSource<T> _writeSource;

    /// <summary>Creates a source writer with an optional default write owner.</summary>
    /// <exception cref="InvalidOperationException">
    /// The configured default source is not registered, does not support writes, or no single
    /// writable root source can be inferred.
    /// </exception>
    public StateSourceWriter(StateSourceSet<T> sourceSet, SourceId? defaultSourceId = null)
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        if (defaultSourceId is { } sourceId && sourceId.IsDefault)
        {
            throw new ArgumentException(
                "A default source ID must be non-empty.",
                nameof(defaultSourceId)
            );
        }

        _writeSource = ResolveSource(sourceSet, defaultSourceId);
    }

    /// <summary>The write source resolved once at construction time.</summary>
    internal StateSource<T> WriteSource => _writeSource;

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        context = ConfiglueResourceContext.Normalize(context);
        var sourceContext = context.IsDefault
            ? context
            : _writeSource.GetResourceContext(context.Subject);
        return _writeSource.WriteAsync(sourceContext, request, cancellationToken);
    }

    private static StateSource<T> ResolveSource(
        StateSourceSet<T> sourceSet,
        SourceId? defaultSourceId
    )
    {
        if (defaultSourceId is { } configuredSourceId)
        {
            StateSource<T>? explicitSource = null;
            foreach (var candidate in sourceSet.Sources)
            {
                if (candidate.Id == configuredSourceId)
                {
                    explicitSource = candidate;
                    break;
                }
            }

            if (explicitSource is null)
            {
                throw new InvalidOperationException(
                    $"State source '{configuredSourceId}' is not registered."
                );
            }

            if (explicitSource.Writer is null)
            {
                throw new InvalidOperationException(
                    $"State source '{explicitSource.Id}' does not support writes."
                );
            }

            return explicitSource;
        }

        StateSource<T>? inferred = null;
        foreach (var candidate in sourceSet.Sources)
        {
            if (
                candidate.Writer is null
                || candidate.ExplicitOnly
                || candidate.OwnedPropertyPaths.Count > 0
            )
            {
                continue;
            }

            if (inferred is not null)
            {
                throw new InvalidOperationException(
                    $"Multiple writable root sources ('{inferred.Id}' and '{candidate.Id}') are registered. Configure a default write owner."
                );
            }

            inferred = candidate;
        }

        if (inferred is null)
        {
            throw new InvalidOperationException("No writable root state source is registered.");
        }

        return inferred;
    }
}
