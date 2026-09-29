using Configlue.Resources;

namespace Configlue.State;

/// <summary>Routes writes independently from read-source selection.</summary>
public sealed class StateSourceWriter<T> : IStateWriter<T>
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
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    ) => WriteCoreAsync(null, request, cancellationToken);

    /// <summary>Writes state to the selected source for one subject.</summary>
    public ValueTask<StateWriteResult> WriteAsync(
        IConfiglueSubject subject,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(subject);
        return WriteCoreAsync(
            new ConfiglueResourceContext(subject, subject.Key),
            request,
            cancellationToken
        );
    }

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    ) => WriteCoreAsync(context, request, cancellationToken);

    private ValueTask<StateWriteResult> WriteCoreAsync(
        ConfiglueResourceContext? context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken
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

        return context is null
            ? source.Writer.WriteAsync(request, cancellationToken)
            : source.WriteAsync(
                source.GetResourceContext(context.Value.Subject, context.Value.Route),
                request,
                cancellationToken
            );
    }
}
