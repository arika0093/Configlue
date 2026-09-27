using Microsoft.Extensions.Logging;

namespace Configlue;

/// <summary>Reads the first successful state from a priority-ordered set of sources.</summary>
public sealed class StateSourceResolver<T> : IStateReader<T>
{
    private static readonly EventId ReadEvent = new(1050, "ResolverSourceRead");
    private static readonly EventId FallbackEvent = new(1051, "ResolverSourceFallback");
    private readonly StateSourceSet<T> _sourceSet;
    private readonly ILogger? _logger;
    private Resolution? _resolution;

    /// <summary>Creates a source resolver.</summary>
    public StateSourceResolver(StateSourceSet<T> sourceSet, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        _sourceSet = sourceSet;
        _logger = logger;
    }

    /// <summary>The source that most recently supplied a value.</summary>
    public StateSource<T>? ActiveSource => Volatile.Read(ref _resolution)?.ActiveSource;

    /// <inheritdoc />
    public async ValueTask<StateReadResult<T>> ReadAsync(
        CancellationToken cancellationToken = default
    )
    {
        StateReadResult<T> lastResult = default;
        var revisions = new List<StateRevision>();
        var nestedRevisions = new List<KeyValuePair<string, StateRevisionVector>>();
        foreach (var source in _sourceSet.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger?.LogTrace(
                ReadEvent,
                "Reading state source {SourceId} at {PhysicalOrigin} ({ResourceId}).",
                source.Id,
                source.PhysicalOrigin,
                source.ResourceId?.Value
            );
            StateReadResult<T> result;
            try
            {
                result = await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // Preserve the source reader's exception type for provider recovery handling.
#pragma warning disable S2139
            catch (Exception exception)
            {
                _logger?.LogError(
                    ReadEvent,
                    exception,
                    "Reading state source {SourceId} failed at {PhysicalOrigin} ({ResourceId}).",
                    source.Id,
                    source.PhysicalOrigin,
                    source.ResourceId?.Value
                );
                throw;
            }
#pragma warning restore S2139

            result = result.FromSource(source.Id, source.PhysicalOrigin);
            _logger?.LogDebug(
                ReadEvent,
                "State source {SourceId} returned {ReadStatus} at {PhysicalOrigin} ({ResourceId}).",
                source.Id,
                result.Status,
                source.PhysicalOrigin,
                source.ResourceId?.Value
            );
            revisions.Add(new StateRevision(source.Id, result.Revision));
            if (result.Revisions is { } nestedVector)
            {
                nestedRevisions.Add(
                    new KeyValuePair<string, StateRevisionVector>(source.Id, nestedVector)
                );
            }

            if (result.Status == StateReadStatus.Success)
            {
                var revisionVector = new StateRevisionVector(revisions, nestedRevisions);
                Volatile.Write(ref _resolution, new Resolution(source, revisionVector));
                return result with { Revisions = revisionVector };
            }

            var canFallBack = CanFallBack(source.FallbackCondition, result.Status);
            _logger?.Log(
                result.Status == StateReadStatus.Unavailable ? LogLevel.Warning : LogLevel.Debug,
                FallbackEvent,
                "State source {SourceId} returned {ReadStatus}; fallback {FallbackAction}.",
                source.Id,
                result.Status,
                canFallBack ? "continues" : "stops"
            );
            if (!canFallBack)
            {
                var revisionVector = new StateRevisionVector(revisions, nestedRevisions);
                Volatile.Write(ref _resolution, new Resolution(null, revisionVector));
                return result with { Revisions = revisionVector };
            }

            lastResult = result;
        }

        var finalVector = new StateRevisionVector(revisions, nestedRevisions);
        Volatile.Write(ref _resolution, new Resolution(null, finalVector));
        return lastResult with { Revisions = finalVector };
    }

    internal IReadOnlyList<StateSourceWatchTarget<T>> GetSourcesForWatch(string? fallbackRevision)
    {
        var resolution = Volatile.Read(ref _resolution);
        var active = resolution?.ActiveSource;
        return _sourceSet
            .Sources.Where(source =>
                resolution is null
                || resolution.Revisions.TryGetRevision(source.Id, out _)
                    && (active is null || source.Priority >= active.Priority)
            )
            .Select(source =>
            {
                string? revision = null;
                if (resolution is not null)
                {
                    resolution.Revisions.TryGetRevision(source.Id, out revision);
                }
                if (resolution is null && source.Id == _sourceSet.Sources[0].Id)
                {
                    revision = fallbackRevision;
                }

                return new StateSourceWatchTarget<T>(source, revision);
            })
            .ToArray();
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

internal readonly record struct StateSourceWatchTarget<T>(
    StateSource<T> Source,
    string? ObservedRevision
);
