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

    /// <summary>Test-only synchronization hooks used to force cache residency interleavings.</summary>
    internal SubjectResolutionCacheTestHooks? SubjectResolutionTestHooks { get; set; }

    /// <inheritdoc />
    public ValueTask<StateReadResult<T>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        context = ConfiglueResourceContext.Normalize(context);
        return ReadCoreAsync(
            context.IsDefault ? null : context.Subject,
            context,
            cancellationToken
        );
    }

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
        else
        {
            var key = (subject.Key, route);
            while (true)
            {
                if (!_subjectResolutions.TryGetValue(key, out var entry))
                {
                    resolution = null;
                    break;
                }

                SubjectResolutionTestHooks?.AfterWatchLookup?.Invoke();
                if (entry.TryAcquireWatchLease(out var leasedResolution, out var lease))
                {
                    resolution = leasedResolution;
                    watchLease = lease;
                    break;
                }
            }
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

        var key = (subject.Key, context.Route);
        var now = Stopwatch.GetTimestamp();
        while (true)
        {
            // The dictionary never hands out an entry we may mutate without re-proving residency.
            // The entry's lock makes "still resident" and "still updatable" the same decision.
            if (_subjectResolutions.TryGetValue(key, out var entry))
            {
                SubjectResolutionTestHooks?.AfterUpdateLookup?.Invoke();
                if (entry.TryUpdate(resolution, now))
                {
                    break;
                }

                continue;
            }

            var candidate = new SubjectResolution(this, key, resolution);
            if (!_subjectResolutions.TryAdd(key, candidate))
            {
                continue;
            }

            if (candidate.TryPublish(now))
            {
                break;
            }
        }

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

    private int SweepSubjectResolutions(long now)
    {
        var idleTicks = _subjectResolutionIdleTicks;
        var evicted = 0;
        foreach (var pair in _subjectResolutions)
        {
            if (!pair.Value.TryEvict(now, idleTicks))
            {
                continue;
            }

            evicted++;
            _logger?.LogTrace(SubjectCacheEvictionEvent, "Evicted an idle subject resolution.");
        }

        return evicted;
    }

    /// <summary>Test-only hook that forces an idle sweep at the current timestamp.</summary>
    internal int EvictIdleSubjectResolutionsForTest() =>
        SweepSubjectResolutions(Stopwatch.GetTimestamp());

    /// <summary>
    /// Test-only hook that evicts a resident entry even while a watch lease is held so the behavior of stale
    /// leases can be observed.
    /// </summary>
    internal bool ForceEvictSubjectResolutionForTest(IConfiglueSubject subject, RouteKey route)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return _subjectResolutions.TryGetValue((subject.Key, route), out var entry)
            && entry.ForceEvictForTest();
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

    private enum SubjectResolutionState
    {
        Active,
        Evicting,
        Evicted,
    }

    /// <summary>
    /// A resident-or-detached per-subject resolution. Residency, updating and watch leases are all decided under
    /// <c>_gate</c> so a caller holding a reference to an evicted entry can never mutate or lease it.
    /// </summary>
    private sealed class SubjectResolution
    {
        private readonly StateSourceResolver<T> _owner;
        private readonly (SubjectKey SubjectKey, RouteKey Route) _key;
        private readonly object _gate = new();
        private Resolution _resolution;
        private long _lastAccessTimestamp;
        private int _watchReferenceCount;
        private bool _counted;
        private SubjectResolutionState _state = SubjectResolutionState.Active;

        public SubjectResolution(
            StateSourceResolver<T> owner,
            (SubjectKey SubjectKey, RouteKey Route) key,
            Resolution resolution
        )
        {
            _owner = owner;
            _key = key;
            _resolution = resolution;
            _lastAccessTimestamp = Stopwatch.GetTimestamp();
        }

        /// <summary>Counts a freshly added entry once it is proven to still be resident.</summary>
        public bool TryPublish(long timestamp)
        {
            lock (_gate)
            {
                if (_state != SubjectResolutionState.Active)
                {
                    return false;
                }

                if (!_counted)
                {
                    _counted = true;
                    Interlocked.Increment(ref _owner._subjectResolutionCount);
                }

                _lastAccessTimestamp = timestamp;
                return true;
            }
        }

        /// <summary>Updates the entry only while it is still the resident entry for its key.</summary>
        public bool TryUpdate(Resolution resolution, long timestamp)
        {
            lock (_gate)
            {
                if (_state != SubjectResolutionState.Active)
                {
                    return false;
                }

                _resolution = resolution;
                _lastAccessTimestamp = timestamp;
                return true;
            }
        }

        /// <summary>Acquires a watch lease on the resident entry, failing once eviction has begun.</summary>
        public bool TryAcquireWatchLease(out Resolution resolution, out IDisposable lease)
        {
            lock (_gate)
            {
                if (_state != SubjectResolutionState.Active)
                {
                    resolution = null!;
                    lease = null!;
                    return false;
                }

                _watchReferenceCount++;
                _lastAccessTimestamp = Stopwatch.GetTimestamp();
                resolution = _resolution;
                lease = new WatchLease(this);
                return true;
            }
        }

        public bool TryEvict(long now, long idleTicks)
        {
            lock (_gate)
            {
                if (_state != SubjectResolutionState.Active)
                {
                    return false;
                }

                if (_watchReferenceCount != 0)
                {
                    return false;
                }

                if (now - _lastAccessTimestamp <= idleTicks)
                {
                    return false;
                }

                return Detach();
            }
        }

        /// <summary>Test-only eviction that ignores live watch leases to exercise stale lease release.</summary>
        public bool ForceEvictForTest()
        {
            lock (_gate)
            {
                if (_state != SubjectResolutionState.Active)
                {
                    return false;
                }

                return Detach();
            }
        }

        /// <summary>
        /// Marks the entry evicting and removes it from the cache under the entry lock, so residency and state
        /// transition together and no caller can observe an evicting entry as still updatable or leasable.
        /// </summary>
        private bool Detach()
        {
            _state = SubjectResolutionState.Evicting;

            // Decrement before handing the key off so the replacement entry cannot be counted while this one is
            // still counted; that keeps the approximate count from over-reporting during a key handoff.
            var wasCounted = _counted;
            if (wasCounted)
            {
                _counted = false;
                Interlocked.Decrement(ref _owner._subjectResolutionCount);
            }

            // The state machine and this lock prove this entry is the resident value for the key (only Detach
            // removes entries), so the key-only removal cannot detach a replacement entry.
            if (!_owner._subjectResolutions.TryRemove(_key, out _))
            {
                _state = SubjectResolutionState.Active;
                if (wasCounted)
                {
                    _counted = true;
                    Interlocked.Increment(ref _owner._subjectResolutionCount);
                }

                return false;
            }

            _state = SubjectResolutionState.Evicted;
            return true;
        }

        private void ReleaseWatchReference()
        {
            _owner.SubjectResolutionTestHooks?.BeforeWatchLeaseRelease?.Invoke();
            lock (_gate)
            {
                _watchReferenceCount--;
            }
        }

        private sealed class WatchLease(SubjectResolution owner) : IDisposable
        {
            private SubjectResolution? _owner = owner;

            public void Dispose() =>
                Interlocked.Exchange(ref _owner, null)?.ReleaseWatchReference();
        }
    }
}

/// <summary>Internal synchronization hooks used by tests to force cache residency interleavings.</summary>
internal sealed class SubjectResolutionCacheTestHooks
{
    /// <summary>Invoked after a resolver update has looked up a resident entry and before it updates it.</summary>
    public Action? AfterUpdateLookup { get; set; }

    /// <summary>Invoked after a watcher has looked up a resident entry and before it leases it.</summary>
    public Action? AfterWatchLookup { get; set; }

    /// <summary>Invoked before a watch lease releases its reference to the entry.</summary>
    public Action? BeforeWatchLeaseRelease { get; set; }
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
