using System.Collections.Concurrent;
using System.Diagnostics;
using Configlue.Resources;
using Configlue.Sources;
using Microsoft.Extensions.Logging;

namespace Configlue.State;

/// <summary>Reads the first successful state from a priority-ordered set of sources.</summary>
/// <remarks>
/// <para>
/// Internal composition implementation (see issue #225). The supported extension surface is
/// <c>ISourceReader{T}</c>, <c>ISourceWriter{T}</c>, <c>ISourceWatcher</c>, and
/// <c>ISourceCapabilities{T}</c>; composition is exposed through source registration
/// (<c>StateSourceSetBuilder{T}</c>, <c>StateSource{T}</c>,
/// <c>CompositeStateSource{TFragment}</c>) rather than by constructing this type directly.
/// </para>
/// <para>
/// Per-subject resolutions are cached so routing and watcher fan-out stay stable across reads. The cache is
/// bounded: entries are evicted once they have been idle for <see cref="SubjectResolutionIdleTimeout"/> and no
/// active watch still references them. Eviction is opportunistic and performed on access, so the resolver does
/// not run a timer or task per subject.
/// </para>
/// </remarks>
internal sealed class StateSourceResolver<T> : ISourceReader<T>
{
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
    private readonly Func<long> _getTimestamp;
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
    /// <param name="getTimestamp">An optional monotonic timestamp source in Stopwatch ticks.</param>
    public StateSourceResolver(
        StateSourceSet<T> sourceSet,
        ILogger? logger = null,
        TimeSpan? subjectResolutionIdleTimeout = null,
        int subjectResolutionSweepThreshold = SubjectResolutionSweepThreshold,
        Func<long>? getTimestamp = null
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
        _getTimestamp = getTimestamp ?? Stopwatch.GetTimestamp;
        _subjectResolutionSweepThreshold = subjectResolutionSweepThreshold;
        _subjectResolutionIdleTicks = (long)(
            idleTimeout.TotalMilliseconds * Stopwatch.Frequency / 1000.0
        );
        _subjectResolutionSweepCountdown = subjectResolutionSweepThreshold;
        _subjectResolutionLastSweepTimestamp = _getTimestamp();
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
        StateReadResult<T> lastResult = default;
        // Scratch observation state is freshly allocated per read and never retained. The
        // retained topology and unchanged revision observations are reused from the
        // previous resolution; only changed reads allocate a new revision vector.
        // Per-read scratch arrays are small Gen0 allocations (issue #276): pooling them
        // saved on the order of a hundred bytes per multi-source read while adding
        // Rent/Clear/Return discipline to every read, which is negligible within
        // budgets dominated by source I/O.
        var revisions = new StateRevision[_sourceSet.Count];
        var revisionCount = 0;
        var watchScratch = new WatchScratchEntry[_sourceSet.Count];
        var watchTargetCount = 0;
        var previousResolution = PeekResolution(subject, context);
        var previousTopology = previousResolution?.Topology;
        var topologyMatches = previousTopology is not null;
        KeyValuePair<SourceId, StateRevisionVector>[]? nestedRevisions = null;
        var nestedRevisionCount = 0;
        for (var index = 0; index < _sourceSet.Count; index++)
        {
            var source = _sourceSet[index];
            cancellationToken.ThrowIfCancellationRequested();
            var effectiveContext = GetEffectiveContext(source, subject, context);
            var result = (
                await ReadSourceAsync(source, effectiveContext, cancellationToken)
                    .ConfigureAwait(false)
            ).FromSource(source.Id, source.PhysicalOrigin);
            watchScratch[watchTargetCount] = new WatchScratchEntry(
                source,
                effectiveContext,
                result.Revision,
                result.Revisions
            );
            if (topologyMatches)
            {
                topologyMatches = TopologyMatches(
                    previousTopology!,
                    watchTargetCount,
                    source,
                    effectiveContext
                );
            }

            watchTargetCount++;
            if (_logger is { } readLogger)
            {
                ResolverLogging.Read(readLogger, source.Id, result.Status, null);
            }
            revisions[revisionCount++] = new StateRevision(source.Id, result.Revision);
            if (result.Revisions is { } nestedVector)
            {
                nestedRevisions ??= new KeyValuePair<SourceId, StateRevisionVector>[
                    _sourceSet.Count
                ];
                nestedRevisions[nestedRevisionCount++] = new(source.Id, nestedVector);
            }

            if (result.Status == StateReadStatus.Success)
            {
                var resolution = CreateOrReuseResolution(
                    previousResolution,
                    topologyMatches,
                    watchScratch,
                    watchTargetCount,
                    source,
                    revisions,
                    nestedRevisions,
                    nestedRevisionCount
                );
                SetResolution(subject, context, resolution);
                return result with { Revisions = resolution.Revisions };
            }

            var canFallBack = CanFallBack(source.FallbackCondition, result.Status);
            if (_logger is { } fallbackLogger)
            {
                ResolverLogging.Fallback(fallbackLogger, source.Id, result.Status, canFallBack);
            }
            if (!canFallBack)
            {
                var resolution = CreateOrReuseResolution(
                    previousResolution,
                    topologyMatches,
                    watchScratch,
                    watchTargetCount,
                    null,
                    revisions,
                    nestedRevisions,
                    nestedRevisionCount
                );
                SetResolution(subject, context, resolution);
                return result with { Revisions = resolution.Revisions };
            }

            lastResult = result;
        }

        var finalResolution = CreateOrReuseResolution(
            previousResolution,
            topologyMatches,
            watchScratch,
            watchTargetCount,
            null,
            revisions,
            nestedRevisions,
            nestedRevisionCount
        );
        SetResolution(subject, context, finalResolution);
        return lastResult with { Revisions = finalResolution.Revisions };
    }

    private static ConfiglueResourceContext GetEffectiveContext(
        StateSource<T> source,
        IConfiglueSubject? subject,
        ConfiglueResourceContext context
    ) => subject is null ? context : source.GetResourceContext(subject);

    private struct WatchScratchEntry
    {
        public StateSource<T>? Source;
        public ConfiglueResourceContext Context;
        public string? Revision;
        public StateRevisionVector? NestedRevisions;

        public WatchScratchEntry(
            StateSource<T>? source,
            ConfiglueResourceContext context,
            string? revision,
            StateRevisionVector? nestedRevisions
        )
        {
            Source = source;
            Context = context;
            Revision = revision;
            NestedRevisions = nestedRevisions;
        }
    }

    private static bool TopologyMatches(
        ResolverWatchTopology<T> topology,
        int index,
        StateSource<T> source,
        ConfiglueResourceContext context
    )
    {
        if ((uint)index >= (uint)topology.Count)
        {
            return false;
        }

        return ReferenceEquals(topology.GetSource(index), source)
            && topology.GetContext(index).Equals(context);
    }

    private static Resolution CreateOrReuseResolution(
        Resolution? previousResolution,
        bool topologyMatches,
        WatchScratchEntry[] scratch,
        int count,
        StateSource<T>? activeSource,
        StateRevision[] revisions,
        KeyValuePair<SourceId, StateRevisionVector>[]? nestedRevisions,
        int nestedRevisionCount
    )
    {
        if (
            topologyMatches
            && previousResolution is not null
            && previousResolution.Topology.Count == count
            && ReferenceEquals(previousResolution.ActiveSource, activeSource)
        )
        {
            var observationsMatch = true;
            for (var index = 0; index < count; index++)
            {
                var previousRevision =
                    count == 1
                        ? previousResolution.SingleObservedRevision
                        : previousResolution.ObservedRevisions![index];
                previousResolution.Revisions.TryGetNestedRevisions(
                    scratch[index].Source!.Id,
                    out var nested
                );
                if (
                    !string.Equals(
                        previousRevision,
                        scratch[index].Revision,
                        StringComparison.Ordinal
                    ) || !ReferenceEquals(nested, scratch[index].NestedRevisions)
                )
                {
                    observationsMatch = false;
                    break;
                }
            }

            if (observationsMatch)
            {
                return previousResolution;
            }
        }

        return CreateResolution(
            previousResolution?.Topology,
            topologyMatches,
            scratch,
            count,
            activeSource,
            CreateRevisionVector(revisions, count, nestedRevisions, nestedRevisionCount)
        );
    }

    private static Resolution CreateResolution(
        ResolverWatchTopology<T>? previousTopology,
        bool topologyMatches,
        WatchScratchEntry[] scratch,
        int count,
        StateSource<T>? activeSource,
        StateRevisionVector revisionVector
    )
    {
        if (topologyMatches && previousTopology is not null && previousTopology.Count == count)
        {
            if (count == 1)
            {
                return new Resolution(
                    activeSource,
                    revisionVector,
                    previousTopology,
                    scratch[0].Revision
                );
            }

            var retainedRevisions = new string?[count];
            for (var index = 0; index < count; index++)
            {
                retainedRevisions[index] = scratch[index].Revision;
            }

            return new Resolution(
                activeSource,
                revisionVector,
                previousTopology,
                retainedRevisions
            );
        }

        if (count == 1)
        {
            var singleTopology = new ResolverWatchTopology<T>(
                scratch[0].Source!,
                scratch[0].Context
            );
            return new Resolution(
                activeSource,
                revisionVector,
                singleTopology,
                scratch[0].Revision
            );
        }

        var routes = new WatchRoute<T>[count];
        var observedRevisions = new string?[count];
        for (var index = 0; index < count; index++)
        {
            routes[index] = new WatchRoute<T>(scratch[index].Source!, scratch[index].Context);
            observedRevisions[index] = scratch[index].Revision;
        }

        return new Resolution(
            activeSource,
            revisionVector,
            new ResolverWatchTopology<T>(routes),
            observedRevisions
        );
    }

    private Resolution? PeekResolution(IConfiglueSubject? subject, ConfiglueResourceContext context)
    {
        if (subject is null)
        {
            return Volatile.Read(ref _resolution);
        }

        if (!_subjectResolutions.TryGetValue((subject.Key, context.Route), out var entry))
        {
            return null;
        }

        return entry.TryPeek();
    }

    internal ResolverWatchTopology<T>? GetWatchTopologyForTest(
        IConfiglueSubject? subject,
        RouteKey route
    )
    {
        if (subject is null)
        {
            return Volatile.Read(ref _resolution)?.Topology;
        }

        if (!_subjectResolutions.TryGetValue((subject.Key, route), out var entry))
        {
            return null;
        }

        return entry.TryPeek()?.Topology;
    }

    private async ValueTask<StateReadResult<T>> ReadSourceAsync(
        StateSource<T> source,
        ConfiglueResourceContext context,
        CancellationToken cancellationToken
    )
    {
        if (_logger is { } logger)
        {
            ResolverLogging.ReadStarted(logger, source.Id, null);
        }
        try
        {
            return await source.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Preserve the source reader's exception type for provider recovery handling.
        catch (Exception exception)
        {
            if (_logger is { } errorLogger && errorLogger.IsEnabled(LogLevel.Error))
            {
                ResolverLogging.ReadFailed(
                    errorLogger,
                    source.Id,
                    exception.GetType().FullName,
                    null
                );
            }
            throw;
        }
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
        if (resolution is not null)
        {
            for (var index = 0; index < resolution.WatchTargetCount; index++)
            {
                var target = resolution.GetWatchTarget(index);
                if (active is not null && target.Source.Priority < active.Priority)
                {
                    continue;
                }

                sources.Add(target);
            }
        }
        else
        {
            for (var index = 0; index < _sourceSet.Count; index++)
            {
                var source = _sourceSet[index];
                sources.Add(
                    new StateSourceWatchTarget<T>(
                        source,
                        GetEffectiveContext(source, subject, ConfiglueResourceContext.Default),
                        index == 0 ? fallbackRevision : null
                    )
                );
            }
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
        var now = _getTimestamp();
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
    internal int EvictIdleSubjectResolutionsForTest() => SweepSubjectResolutions(_getTimestamp());

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
        KeyValuePair<SourceId, StateRevisionVector>[]? nestedRevisions,
        int nestedRevisionCount
    ) =>
        nestedRevisions is null
            ? StateRevisionVector.FromSpan(revisions.AsSpan(0, revisionCount))
            : StateRevisionVector.FromSpan(
                revisions.AsSpan(0, revisionCount),
                nestedRevisions.AsSpan(0, nestedRevisionCount)
            );

    private sealed class Resolution
    {
        public StateSource<T>? ActiveSource { get; }
        public StateRevisionVector Revisions { get; }
        public ResolverWatchTopology<T> Topology { get; }
        public string? SingleObservedRevision { get; }
        public string?[]? ObservedRevisions { get; }

        public Resolution(
            StateSource<T>? activeSource,
            StateRevisionVector revisions,
            ResolverWatchTopology<T> topology,
            string? singleObservedRevision
        )
        {
            ActiveSource = activeSource;
            Revisions = revisions;
            Topology = topology;
            SingleObservedRevision = singleObservedRevision;
        }

        public Resolution(
            StateSource<T>? activeSource,
            StateRevisionVector revisions,
            ResolverWatchTopology<T> topology,
            string?[] observedRevisions
        )
        {
            ActiveSource = activeSource;
            Revisions = revisions;
            Topology = topology;
            ObservedRevisions = observedRevisions;
        }

        public int WatchTargetCount => Topology.Count;

        public StateSourceWatchTarget<T> GetWatchTarget(int index) =>
            Topology.Count == 1
                ? new StateSourceWatchTarget<T>(
                    Topology.GetSource(0),
                    Topology.GetContext(0),
                    SingleObservedRevision
                )
                : new StateSourceWatchTarget<T>(
                    Topology.GetSource(index),
                    Topology.GetContext(index),
                    ObservedRevisions![index]
                );

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
            _lastAccessTimestamp = _owner._getTimestamp();
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
                _lastAccessTimestamp = _owner._getTimestamp();
                resolution = _resolution;
                lease = new WatchLease(this);
                return true;
            }
        }

        /// <summary>
        /// Returns the current resolution without acquiring a watch lease. The returned topology is
        /// immutable, so reusing it for the next read is safe even if this entry is evicted concurrently.
        /// </summary>
        public Resolution? TryPeek()
        {
            lock (_gate)
            {
                if (_state != SubjectResolutionState.Active)
                {
                    return null;
                }

                return _resolution;
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
    public ConfiglueResourceContext EffectiveContext { get; init; }
    public string? ObservedRevision { get; init; }

    public StateSourceWatchTarget(
        StateSource<T> Source,
        ConfiglueResourceContext EffectiveContext,
        string? ObservedRevision
    )
    {
        this.Source = Source;
        this.EffectiveContext = EffectiveContext;
        this.ObservedRevision = ObservedRevision;
    }

    public void Deconstruct(out StateSource<T> Source, out string? ObservedRevision)
    {
        Source = this.Source;
        ObservedRevision = this.ObservedRevision;
    }
}

/// <summary>
/// One immutable watch route: the source plus the effective context used to read it.
/// Revision state is deliberately excluded so identical routing can be shared across reads.
/// </summary>
internal readonly record struct WatchRoute<T>(
    StateSource<T> Source,
    ConfiglueResourceContext EffectiveContext
);

/// <summary>
/// Immutable reusable watch topology: the ordered sources and effective contexts inspected by
/// one resolution, without per-resolution observed revisions. Single-target topologies are stored
/// inline with no heap array; multi-target topologies own one exact-size route array that is never
/// mutated or pooled after publication.
/// </summary>
internal sealed class ResolverWatchTopology<T>
{
    private readonly StateSource<T>? _singleSource;
    private readonly ConfiglueResourceContext _singleContext;
    private readonly WatchRoute<T>[]? _routes;

    public ResolverWatchTopology(StateSource<T> source, ConfiglueResourceContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        Count = 1;
        _singleSource = source;
        _singleContext = context;
    }

    public ResolverWatchTopology(WatchRoute<T>[] routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.Length == 0)
        {
            throw new ArgumentException(
                "A watch topology requires at least one route.",
                nameof(routes)
            );
        }

        Count = routes.Length;
        _routes = routes;
    }

    public int Count { get; }

    internal bool UsesRetainedArray => _routes is not null;

    public StateSource<T> GetSource(int index) =>
        _routes is null ? _singleSource! : _routes[index].Source;

    public ConfiglueResourceContext GetContext(int index) =>
        _routes is null ? _singleContext : _routes[index].EffectiveContext;
}
