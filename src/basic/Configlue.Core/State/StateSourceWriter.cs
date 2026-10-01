using Configlue.Resources;
using Configlue.Sources;

namespace Configlue.State;

/// <summary>Routes writes independently from read-source selection.</summary>
public sealed class StateSourceWriter<T> : ISourceWriter<T>
{
    private readonly StateSourceSet<T> _sourceSet;
    private readonly StateWriteRoute _route;

    /// <summary>Creates a source writer using the highest-priority writable source by default.</summary>
    public StateSourceWriter(StateSourceSet<T> sourceSet, StateWriteRoute route = default)
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        _sourceSet = sourceSet;
        _route = route;
    }

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    )
    {
        var source = _route.SourceId is { } id
            ? _sourceSet.Sources.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.Ordinal)
            )
            : _sourceSet.Sources.FirstOrDefault(static candidate => candidate.Writer is not null);

        if (source is null)
        {
            throw new InvalidOperationException(
                _route.SourceId is { } sourceId
                    ? $"State source '{sourceId}' is not registered."
                    : "No writable state source is registered."
            );
        }

        if (source.Writer is null)
        {
            throw new InvalidOperationException(
                $"State source '{source.Id}' does not support writes."
            );
        }

        var sourceContext = ReferenceEquals(
            context.Subject,
            ConfiglueResourceContext.DefaultSubject
        )
            ? context
            : source.GetResourceContext(context.Subject);
        return source.WriteAsync(sourceContext, request, cancellationToken);
    }
}
