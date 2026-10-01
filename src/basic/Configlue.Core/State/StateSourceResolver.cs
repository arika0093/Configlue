using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Configlue.Resources;
using Configlue.Sources;
using Microsoft.Extensions.Logging;

namespace Configlue.State;

/// <summary>Reads the first successful state from a priority-ordered set of sources.</summary>
/// <remarks>
/// Per-subject resolutions are cached so routing and watcher fan-out stay stable across reads. The cache is
/// bounded: entries are evicted once they have been idle for <see cref="SubjectResolutionIdleTimeout"/> and no
/// active watch still references them. Eviction is opportunistic and performed on access, so the resolver does
/// not run a timer or task per subject.
/// </remarks>
public sealed class StateSourceResolver<T> : ISourceReader<T>
{
    private static readonly EventId ReadEvent = new(1050, "ResolverSourceRead");
    private static readonly EventId FallbackEvent = new(1051, "ResolverSourceFallback");
    private static readonly EventId SubjectCacheEvictionEvent = new(
        1052,
        "ResolverSubjectCacheEviction"
    );

    /// <summary>The idle timeout applied to per-subject resolutions when none is supplied.</summary>
    public static readonly TimeSpan SubjectResolutionIdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The entry count at which opportunistic sweeps are considered even before the idle timeout.</summary>
    public const int SubjectResolutionSweepThreshold = 256;

    private readonly StateSourceSet<T> _sourceSet;
    private readonly ILogger? _logger;
    private Resolution? _resolution;
    private readonly ConcurrentDictionary<
        (SubjectKey SubjectKey, RouteKey Route),
        SubjectResolution
    > _subjectResolutions = new();
    private readonly long _subjectResolutionIdleTicks;
    private readonly int _subjectResolutionSweepThreshold;
    private int _subjectResolutionCount;
    private int _subjectResolutionSweepCountdown;
    private long _subjectResolutionLastSweepTimestamp;

    /// <summary>Creates a source resolver.</summary>
    /// <param name="sourceSet">The priority-ordered sources to resolve.</param>
    /// <param name="logger">An optional logger.</param>
    /// <param name="subjectResolutionIdleTimeout">
    /// Optional idle timeout for cached per-subject resolutions. Defaults to
    /// <see cref="SubjectResolutionIdleTimeout"/>. Use <see cref="TimeSpan.Zero"/> to evict any entry that was
    /// not touched since the previous access.
    /// </param>
    /// <param name="subjectResolutionSweepThreshold">
    /// The approximate cached entry count at which sweeps are attempted on access even before the idle timeout
    /// elapses. Defaults to <see cref="SubjectResolutionSweepThreshold"/>.
    /// </param>
    public StateSourceResolver(
        StateSourceSet<T> sourceSet,
        ILogger? logger = null,
        TimeSpan? subjectResolutionIdleTimeout = null,
        int subjectResolutionSweepThreshold = SubjectResolutionSweepThreshold
    )
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        if (subjectResolutionSweepThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(subjectResolutionSweepThreshold));
        }

        var idleTimeout = subjectResolutionIdleTimeout ?? SubjectResolutionIdleTimeout;
        if (idleTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(subjectResolutionIdleTimeout));
        }

        _sourceSet = sourceSet;
        _logger = logger;
        _subjectResolutionSweepThreshold = subjectResolutionSweepThreshold;
        _subjectResolutionIdleTicks = (long)(
            idleTimeout.TotalMilliseconds * Stopwatch.Frequency / 1000.0
        );
        _subjectResolutionSweepCountdown = subjectResolutionSweepThreshold;
        _subjectResolutionLastSweepTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>The source that most recently supplied a value.</summary>
    public StateSource<T>? ActiveSource => Volatile.Read(ref _resolution)?.ActiveSource;

    /// <summary>The approximate number of cached per-subject resolutions.</summary>
    internal int SubjectResolutionCount => Volatile.Read(ref _subjectResolutionCount);

    /// <inheritdoc />
    public ValueTask<StateReadResult<T>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    ) =>
        ReadCoreAsync(
            ReferenceEquals(context.Subject, ConfiglueResourceContext.DefaultSubject)
                ? null
                : context.Subject,
            context,
            cancellationToken
        );

    private async ValueTask<StateReadResult<T>> ReadCoreAsync(
        IConfiglueSubject? subject,
        ConfiglueResourceContext context,
        CancellationToken cancellationToken
    )
    {
        if (_sourceSet.Count == 1)
        {
            return await ReadSingleSourceAsync(_sourceSet[0], subject, context, cancellationToken)
                .ConfigureAwait(false);
        }

        StateReadResult<T> lastResult = default;
        var revisions = new StateRevision[_sourceSet.Count];
        var revisionCount = 0;
        List<KeyValuePair<string, StateRevisionVector>>? nestedRevisions = null;
        for (var index = 0; index < _sourceSet.Count; index++)
        {
            var source = _sourceSet[index];
            cancellationToken.ThrowIfCancellationRequested();
            var result = (
                await ReadSourceAsync(source, subject, context, cancellationToken)
                    .ConfigureAwait(false)
            ).FromSource(source.Id, source.PhysicalOrigin);
            _logger?.LogDebug(
                ReadEvent,
                "State source {SourceId} returned {ReadStatus}.",
                source.Id,
                result.Status
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
                SetResolution(subject, context, new Resolution(source, revisionVector));
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
                SetResolution(subject, context, new Resolution(null, revisionVector));
                return result with { Revisions = revisionVector };
            }

            lastResult = result;
        }

        var finalVector = CreateRevisionVector(revisions, revisionCount, nestedRevisions);
        SetResolution(subject, context, new Resolution(null, finalVector));
        return lastResult with { Revisions = finalVector };
    }

    private async ValueTask<StateReadResult<T>> ReadSingleSourceAsync(
        StateSource<T> source,
        IConfiglueSubject? subject,
        ConfiglueResourceContext context,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = (
            await ReadSourceAsync(source, subject, context, cancellationToken).ConfigureAwait(false)
        ).FromSource(source.Id, source.PhysicalOrigin);
        _logger?.LogDebug(
            ReadEvent,
            "State source {SourceId} returned {ReadStatus}.",
            source.Id,
            result.Status
        );
        var revision = new StateRevision(source.Id, result.Revision);
        StateRevisionVector revisionVector;
        if (result.Revisions is { } nestedVector)
        {
            var nestedEntry = new KeyValuePair<string, StateRevisionVector>(
                source.Id,
                nestedVector
            );
#if NETSTANDARD
            revisionVector = StateRevisionVector.FromSpan(
                new[] { revision },
                new[] { nestedEntry }
            );
#else
            revisionVector = StateRevisionVector.FromSpan(
                MemoryMarshal.CreateReadOnlySpan(ref revision, 1),
                MemoryMarshal.CreateReadOnlySpan(ref nestedEntry, 1)
            );
#endif
        }
        else
        {
#if NETSTANDARD
            revisionVector = StateRevisionVector.FromSpan(new[] { revision });
#else
            revisionVector = StateRevisionVector.FromSpan(
                MemoryMarshal.CreateReadOnlySpan(ref revision, 1)
            );
#endif
        }

        SetResolution(
            subject,
            context,
            new Resolution(result.Status == StateReadStatus.Success ? source : null, revisionVector)
        );
        return result with { Revisions = revisionVector };
    }

    private async ValueTask<StateReadResult<T>> ReadSourceAsync(
        StateSource<T> source,
        IConfiglueSubject? subject,
        ConfiglueResourceContext context,
        CancellationToken cancellationToken
    )
    {
        _logger?.LogTrace(ReadEvent, "Reading state source {SourceId}.", source.Id);
        try
        {
            return await source
                .ReadAsync(
                    subject is null ? context : source.GetResourceContext(subject),
                    cancellationToken
                )
                .ConfigureAwait(false);
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
                "Reading state source {SourceId} failed ({ErrorCategory}).",
                source.Id,
                exception.GetType().FullName
            );
            throw;
        }
#pragma warning restore S2139
    }

    internal StateSourceWatchTargets<T> GetSourcesForWatch(string? fallbackRevision)
    {
        return GetSourcesForWatchCore(null, RouteKey.Default, fallbackRevision);
    }

    internal StateSourceWatchTargets<T> GetSourcesForWatch(
        IConfiglueSubject subject,
        RouteKey route,
        string? fallbackRevision
    ) => GetSourcesForWatchCore(subject, route, fallbackRevision);

    private StateSourceWatchTargets<T> GetSourcesForWatchCore(
        IConfiglueSubject? subject,
        RouteKey route,
        string? fallbackRevision
    )
    {
        Resolution? resolution;
        IDisposable? watchLease = null;
        if (subject is null)
        {
            resolution = Volatile.Read(ref _resolution);
        }
        else if (_subjectResolutions.TryGetValue((subject.Key, route), out var entry))
        {
            watchLease = entry.AcquireWatchLease();
            resolution = entry.Resolution;
        }
        else
        {
            resolution = null;
        }

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

        return new StateSourceWatchTargets<T>(sources, watchLease);
    }

    private void SetResolution(
        IConfiglueSubject? subject,
        ConfiglueResourceContext context,
        Resolution resolution
    )
    {
        if (subject is null)
        {
            Volatile.Write(ref _resolution, resolution);
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (!_subjectResolutions.TryGetValue((subject.Key, context.Route), out var entry))
        {
            var candidate = new SubjectResolution(resolution);
            entry = _subjectResolutions.GetOrAdd((subject.Key, context.Route), candidate);
            if (ReferenceEquals(entry, candidate))
            {
                Interlocked.Increment(ref _subjectResolutionCount);
            }
        }

        entry.Update(resolution, now);
        MaybeSweepSubjectResolutions(now);
    }

    private void MaybeSweepSubjectResolutions(long now)
    {
        var idleElapsed =
            now - Volatile.Read(ref _subjectResolutionLastSweepTimestamp)
            > _subjectResolutionIdleTicks;
        var atCapacity =
            Volatile.Read(ref _subjectResolutionCount) >= _subjectResolutionSweepThreshold;
        if (!idleElapsed && !atCapacity)
        {
            return;
        }

        // Capacity-driven sweeps are amortized so a large live set is not rescanned on every access.
        if (
            atCapacity
            && !idleElapsed
            && Interlocked.Decrement(ref _subjectResolutionSweepCountdown) > 0
        )
        {
            return;
        }

        Interlocked.Exchange(
            ref _subjectResolutionSweepCountdown,
            _subjectResolutionSweepThreshold
        );
        Volatile.Write(ref _subjectResolutionLastSweepTimestamp, now);
        SweepSubjectResolutions(now);
    }

    private void SweepSubjectResolutions(long now)
    {
        var idleTicks = _subjectResolutionIdleTicks;
        foreach (var pair in _subjectResolutions)
        {
            if (
                pair.Value.IsEvictable(now, idleTicks)
                && _subjectResolutions.TryRemove(pair.Key, out _)
            )
            {
                Interlocked.Decrement(ref _subjectResolutionCount);
                _logger?.LogTrace(SubjectCacheEvictionEvent, "Evicted an idle subject resolution.");
            }
        }
    }

    private static bool CanFallBack(StateFallbackCondition condition, StateReadStatus status) =>
        status switch
        {
            StateReadStatus.NotFound => (condition & StateFallbackCondition.NotFound) != 0,
            StateReadStatus.Unavailable => (condition & StateFallbackCondition.Unavailable) != 0,
            StateReadStatus.InvalidPayload => (condition & StateFallbackCondition.InvalidPayload)
                != 0,
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
#if NETSTANDARD
                nestedRevisions.ToArray()
#else
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(nestedRevisions)
#endif
            );

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

    private sealed class SubjectResolution
    {
        private Resolution _resolution;
        private long _lastAccessTimestamp;
        private int _watchReferenceCount;

        public SubjectResolution(Resolution resolution)
        {
            _resolution = resolution;
            _lastAccessTimestamp = Stopwatch.GetTimestamp();
        }

        public Resolution Resolution => Volatile.Read(ref _resolution);

        public void Update(Resolution resolution, long timestamp)
        {
            Volatile.Write(ref _resolution, resolution);
            Volatile.Write(ref _lastAccessTimestamp, timestamp);
        }

        public IDisposable AcquireWatchLease()
        {
            Interlocked.Increment(ref _watchReferenceCount);
            Volatile.Write(ref _lastAccessTimestamp, Stopwatch.GetTimestamp());
            return new WatchLease(this);
        }

        public bool IsEvictable(long now, long idleTicks) =>
            Volatile.Read(ref _watchReferenceCount) == 0
            && now - Volatile.Read(ref _lastAccessTimestamp) > idleTicks;

        private void ReleaseWatchReference() => Interlocked.Decrement(ref _watchReferenceCount);

        private sealed class WatchLease(SubjectResolution owner) : IDisposable
        {
            private SubjectResolution? _owner = owner;

            public void Dispose() =>
                Interlocked.Exchange(ref _owner, null)?.ReleaseWatchReference();
        }
    }
}

/// <summary>
/// A captured set of watch targets plus an optional lease that keeps the underlying subject resolution cached
/// while a watcher is still using it. Disposing the lease releases the active-watch reference.
/// </summary>
internal sealed class StateSourceWatchTargets<T> : IDisposable
{
    private IDisposable? _lease;
    private int _disposed;

    public StateSourceWatchTargets(
        IReadOnlyList<StateSourceWatchTarget<T>> targets,
        IDisposable? lease
    )
    {
        Targets = targets;
        _lease = lease;
    }

    /// <summary>The captured watch targets. The list remains valid after disposal.</summary>
    public IReadOnlyList<StateSourceWatchTarget<T>> Targets { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Interlocked.Exchange(ref _lease, null)?.Dispose();
        }
    }
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
