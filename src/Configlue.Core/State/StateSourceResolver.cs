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
        var revisions = new StateRevision[_sourceSet.Count];
        var revisionCount = 0;
        List<KeyValuePair<string, StateRevisionVector>>? nestedRevisions = null;
        for (var index = 0; index < _sourceSet.Count; index++)
        {
            var source = _sourceSet[index];
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
            revisions[revisionCount++] = new StateRevision(source.Id, result.Revision);
            if (result.Revisions is { } nestedVector)
            {
                nestedRevisions ??= [];
                nestedRevisions.Add(
                    new KeyValuePair<string, StateRevisionVector>(source.Id, nestedVector)
                );
            }

            if (result.Status == StateReadStatus.Success)
            {
                var revisionVector = CreateRevisionVector(
                    revisions,
                    revisionCount,
                    nestedRevisions
                );
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
                var revisionVector = CreateRevisionVector(
                    revisions,
                    revisionCount,
                    nestedRevisions
                );
                Volatile.Write(ref _resolution, new Resolution(null, revisionVector));
                return result with { Revisions = revisionVector };
            }

            lastResult = result;
        }

        var finalVector = CreateRevisionVector(revisions, revisionCount, nestedRevisions);
        Volatile.Write(ref _resolution, new Resolution(null, finalVector));
        return lastResult with { Revisions = finalVector };
    }

    internal IReadOnlyList<StateSourceWatchTarget<T>> GetSourcesForWatch(string? fallbackRevision)
    {
        var resolution = Volatile.Read(ref _resolution);
        var active = resolution?.ActiveSource;
        var sources = new List<StateSourceWatchTarget<T>>(_sourceSet.Count);
        for (var index = 0; index < _sourceSet.Count; index++)
        {
            var source = _sourceSet[index];
            if (
                resolution is not null
                && (
                    !resolution.Revisions.TryGetRevision(source.Id, out _)
                    || (active is not null && source.Priority < active.Priority)
                )
            )
            {
                continue;
            }

            string? revision = null;
            if (resolution is not null)
            {
                resolution.Revisions.TryGetRevision(source.Id, out revision);
            }
            else if (index == 0)
            {
                revision = fallbackRevision;
            }

            sources.Add(new StateSourceWatchTarget<T>(source, revision));
        }

        return sources;
    }

    private sealed record Resolution
    {
        public StateSource<T>? ActiveSource { get; init; }
        public StateRevisionVector Revisions { get; init; }

        public Resolution(StateSource<T>? ActiveSource, StateRevisionVector Revisions)
        {
            this.ActiveSource = ActiveSource;
            this.Revisions = Revisions;
        }

        public void Deconstruct(out StateSource<T>? ActiveSource, out StateRevisionVector Revisions)
        {
            ActiveSource = this.ActiveSource;
            Revisions = this.Revisions;
        }
    }

    private static bool CanFallBack(StateFallbackCondition condition, StateReadStatus status) =>
        status switch
        {
            StateReadStatus.NotFound => (condition & StateFallbackCondition.NotFound) != 0,
            StateReadStatus.Unavailable => (condition & StateFallbackCondition.Unavailable) != 0,
            StateReadStatus.Invalid => (condition & StateFallbackCondition.Invalid) != 0,
            _ => false,
        };

    private static StateRevisionVector CreateRevisionVector(
        StateRevision[] revisions,
        int revisionCount,
        List<KeyValuePair<string, StateRevisionVector>>? nestedRevisions
    ) =>
        nestedRevisions is null
            ? StateRevisionVector.FromSpan(revisions.AsSpan(0, revisionCount))
            : StateRevisionVector.FromSpan(
                revisions.AsSpan(0, revisionCount),
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(nestedRevisions)
            );
}

internal readonly record struct StateSourceWatchTarget<T>
{
    public StateSource<T> Source { get; init; }
    public string? ObservedRevision { get; init; }

    public StateSourceWatchTarget(StateSource<T> Source, string? ObservedRevision)
    {
        this.Source = Source;
        this.ObservedRevision = ObservedRevision;
    }

    public void Deconstruct(out StateSource<T> Source, out string? ObservedRevision)
    {
        Source = this.Source;
        ObservedRevision = this.ObservedRevision;
    }
}
