using Configlue.Resources;
using Configlue.Sources;

namespace Configlue.State;

/// <summary>Routes writes independently from read-source selection using deterministic ownership.</summary>
public sealed class StateSourceWriter<T> : ISourceWriter<T>
{
    private readonly StateSourceSet<T> _sourceSet;
    private readonly string? _defaultSourceId;

    /// <summary>Creates a source writer with an optional default write owner.</summary>
    public StateSourceWriter(StateSourceSet<T> sourceSet, string? defaultSourceId = null)
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        if (defaultSourceId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(defaultSourceId);
        }

        _sourceSet = sourceSet;
        _defaultSourceId = defaultSourceId;
    }

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    )
    {
        var source = ResolveSource();
        var sourceContext = ReferenceEquals(
            context.Subject,
            ConfiglueResourceContext.DefaultSubject
        )
            ? context
            : source.GetResourceContext(context.Subject);
        return source.WriteAsync(sourceContext, request, cancellationToken);
    }

    private StateSource<T> ResolveSource()
    {
        if (_defaultSourceId is { } defaultSourceId)
        {
            var explicitSource = _sourceSet.Sources.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, defaultSourceId, StringComparison.Ordinal)
            );
            if (explicitSource is null)
            {
                throw new InvalidOperationException(
                    $"State source '{defaultSourceId}' is not registered."
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
        foreach (var candidate in _sourceSet.Sources)
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
