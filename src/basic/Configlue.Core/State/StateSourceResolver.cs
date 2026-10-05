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
/// a plain concurrent dictionary of immutable entries: reads replace the entry for their subject, watches
/// capture an immutable snapshot without pinning residency, and idle entries are evicted opportunistically
/// on access once they have been idle for <see cref="SubjectResolutionIdleTimeout"/>. Eviction never runs on
/// a timer or per-subject task. A watch that races eviction simply falls back to the conservative full
/// source list; captured snapshots stay valid after eviction (see issue #271).
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

    private readonly StateSourceSet<T> _sourceSet;
    private readonly ILogger? _logger;
    private readonly Func<long> _getTimestamp;
    private Resolution? _resolution;
    private readonly ConcurrentDictionary<
        (SubjectKey SubjectKey, RouteKey Route),
        SubjectEntry
    > _subjectResolutions = new();
    private readonly long _subjectResolutionIdleTicks;
    private long _subjectResolutionLastSweepTimestamp;

    /// <summary>Creates a source resolver.</summary>
    /// <param name="sourceSet">The priority-ordered sources to resolve.</param>
    /// <param name="logger">An optional logger.</param>
    /// <param name="subjectResolutionIdleTimeout">
    /// Optional idle timeout for cached per-subject resolutions. Defaults to
    /// <see cref="SubjectResolutionIdleTimeout"/>. Use <see cref="TimeSpan.Zero"/> to evict any entry that was
    /// not touched since the previous access.
    /// </param>
    /// <param name="getTimestamp">An optional monotonic timestamp source in Stopwatch ticks.</param>
    public StateSourceResolver(
        StateSourceSet<T> sourceSet,
        ILogger? logger = null,
        TimeSpan? subjectResolutionIdleTimeout = null,
        Func<long>? getTimestamp = null
    )
    {
        ArgumentNullException.ThrowIfNull(sourceSet);

        var idleTimeout = subjectResolutionIdleTimeout ?? SubjectResolutionIdleTimeout;
        if (idleTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(subjectResolutionIdleTimeout));
        }

        _sourceSet = sourceSet;
        _logger = logger;
        _getTimestamp = getTimestamp ?? Stopwatch.GetTimestamp;
        _subjectResolutionIdleTicks = (long)(
            idleTimeout.TotalMilliseconds * Stopwatch.Frequency / 1000.0
        );
        _subjectResolutionLastSweepTimestamp = _getTimestamp();
    }

    /// <summary>The source that most recently supplied a value.</summary>
    public StateSource<T>? ActiveSource => Volatile.Read(ref _resolution)?.ActiveSource;

    /// <summary>The number of cached per-subject resolutions.</summary>
    internal int SubjectResolutionCount => _subjectResolutions.Count;

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

        return entry.Resolution;
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

        return entry.Resolution.Topology;
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
        // A watch captures an immutable snapshot. The entry may be replaced or evicted concurrently;
        // either snapshot is a valid routing/observation pair, and a missing entry falls back to the
        // conservative full source list. Watches deliberately do not pin residency (issue #271).
        Resolution? resolution;
        if (subject is null)
        {
            resolution = Volatile.Read(ref _resolution);
        }
        else if (_subjectResolutions.TryGetValue((subject.Key, route), out var entry))
        {
            resolution = entry.Resolution;
        }
        else
        {
            resolution = null;
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

        return new StateSourceWatchTargets<T>(sources);
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
        // Immutable replacement: concurrent readers/watches each hold a complete snapshot, so the
        // last writer simply wins. The indexer assignment is a single atomic update; no residency
        // protocol is needed to prove an update is current.
        _subjectResolutions[key] = new SubjectEntry(resolution, now);

        MaybeSweepSubjectResolutions(now);
    }

    private void MaybeSweepSubjectResolutions(long now)
    {
        // Idle-only eviction: a sweep runs at most once per idle timeout, so the hot path is a
        // single timestamp comparison and never scales with the cached entry count. A separate
        // capacity trigger was measured (issue #271) to only rescan without evicting anything the
        // next idle sweep would not: entries that are individually idle while the global timeout has
        // not elapsed are evicted at most one idle timeout later, which is immaterial for an
        // opportunistic bound.
        if (
            now - Volatile.Read(ref _subjectResolutionLastSweepTimestamp)
            <= _subjectResolutionIdleTicks
        )
        {
            return;
        }

        Volatile.Write(ref _subjectResolutionLastSweepTimestamp, now);
        SweepSubjectResolutions(now);
    }

    private int SweepSubjectResolutions(long now)
    {
        var idleTicks = _subjectResolutionIdleTicks;
        // Remove by exact key/value pair so an entry replaced concurrently (with a fresh timestamp)
        // is never evicted on behalf of the stale snapshot enumerated here. SubjectEntry is an
        // immutable reference type without value equality, so the pair removal is a reference
        // comparison against the currently resident entry.
        var entries =
            (ICollection<KeyValuePair<(SubjectKey SubjectKey, RouteKey Route), SubjectEntry>>)
                _subjectResolutions;
        var evicted = 0;
        foreach (var pair in _subjectResolutions)
        {
            if (now - pair.Value.LastAccessTimestamp <= idleTicks)
            {
                continue;
            }

            if (!entries.Remove(pair))
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

    /// <summary>
    /// One immutable cached per-subject resolution. Reads publish a new instance via dictionary
    /// replacement; watches and reads each observe one complete snapshot, so no entry-level locking,
    /// residency state machine, or watch reference counting is required.
    /// </summary>
    private sealed class SubjectEntry(Resolution resolution, long lastAccessTimestamp)
    {
        public Resolution Resolution { get; } = resolution;
        public long LastAccessTimestamp { get; } = lastAccessTimestamp;
    }
}

/// <summary>
/// A captured set of watch targets. The targets are an immutable snapshot copied from the resolution
/// observed when the watch started, so the list remains valid even if the underlying subject entry is
/// replaced or evicted while the watch is in flight.
/// </summary>
internal sealed class StateSourceWatchTargets<T> : IDisposable
{
    public StateSourceWatchTargets(IReadOnlyList<StateSourceWatchTarget<T>> targets)
    {
        Targets = targets;
    }

    /// <summary>The captured watch targets. The list remains valid after disposal.</summary>
    public IReadOnlyList<StateSourceWatchTarget<T>> Targets { get; }

    public void Dispose() { }
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
