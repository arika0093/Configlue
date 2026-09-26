namespace Configlue;

/// <summary>Reads the first successful state from a priority-ordered set of sources.</summary>
public sealed class StateSourceResolver<T> : IStateReader<T>
{
    private readonly StateSourceSet<T> _sourceSet;
    private Resolution? _resolution;

    /// <summary>Creates a source resolver.</summary>
    public StateSourceResolver(StateSourceSet<T> sourceSet)
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        _sourceSet = sourceSet;
    }

    /// <summary>The source that most recently supplied a value.</summary>
    public StateSource<T>? ActiveSource => Volatile.Read(ref _resolution)?.ActiveSource;

    /// <inheritdoc />
    public async ValueTask<StateReadResult<T>> ReadAsync(CancellationToken cancellationToken = default)
    {
        StateReadResult<T> lastResult = default;
        var revisions = new List<StateRevision>();
        foreach (var source in _sourceSet.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            result = result.FromSource(source.Id, source.PhysicalOrigin);
            revisions.Add(new StateRevision(source.Id, result.Revision));
            if (result.Status == StateReadStatus.Success)
            {
                var revisionVector = new StateRevisionVector(revisions);
                Volatile.Write(ref _resolution, new Resolution(source, revisionVector));
                return result with { Revisions = revisionVector };
            }

            if (!CanFallBack(source.FallbackCondition, result.Status))
            {
                var revisionVector = new StateRevisionVector(revisions);
                Volatile.Write(ref _resolution, new Resolution(null, revisionVector));
                return result with { Revisions = revisionVector };
            }

            lastResult = result;
        }

        var finalVector = new StateRevisionVector(revisions);
        Volatile.Write(ref _resolution, new Resolution(null, finalVector));
        return lastResult with { Revisions = finalVector };
    }

    internal IReadOnlyList<StateSourceWatchTarget<T>> GetSourcesForWatch(string? fallbackRevision)
    {
        var resolution = Volatile.Read(ref _resolution);
        var active = resolution?.ActiveSource;
        return _sourceSet.Sources
            .Where(source => resolution is null ||
                resolution.Revisions.TryGetRevision(source.Id, out _) && (active is null || source.Priority >= active.Priority))
            .Select(source =>
        {
            string? revision = null;
            var hasRevision = resolution is not null && resolution.Revisions.TryGetRevision(source.Id, out revision);
            if (resolution is null && source.Id == _sourceSet.Sources[0].Id)
            {
                revision = fallbackRevision;
            }

            return new StateSourceWatchTarget<T>(source, revision);
        }).ToArray();
    }

    private sealed record Resolution(StateSource<T>? ActiveSource, StateRevisionVector Revisions);

    private static bool CanFallBack(StateFallbackCondition condition, StateReadStatus status) =>
        status switch
        {
            StateReadStatus.NotFound => (condition & StateFallbackCondition.NotFound) != 0,
            StateReadStatus.Unavailable => (condition & StateFallbackCondition.Unavailable) != 0,
            _ => false,
        };
}

internal readonly record struct StateSourceWatchTarget<T>(StateSource<T> Source, string? ObservedRevision);
