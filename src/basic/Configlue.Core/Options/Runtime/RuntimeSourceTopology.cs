namespace Configlue;

/// <summary>
/// Owns source topology for one runtime: the registered set, the active (non-retired)
/// snapshot, retirement state, the topology-change signal observed by watchers, and
/// the single-source fast-path snapshot.
///
/// All retired-set/topology mutations are guarded by one private gate owned here.
/// Readers observe the active array through a volatile read and never take the gate.
/// </summary>
internal sealed class RuntimeSourceTopology<TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly object _gate = new();
    private readonly StateSourceSet<TFragment> _sourceSet;
    private readonly HashSet<SourceId> _retiredSourceIds = [];
    private readonly Dictionary<SourceId, string> _detailsSourceKeys = [];
    private StateSource<TFragment>[] _activeSources;
    private TaskCompletionSource _sourceTopologyChanged = RuntimeState.NewTopologySignal();
    private bool _isSingleSourceFastPath;
    private StateSource<TFragment>[] _fastPathSources = [];
    private StateSource<TFragment>? _fastPathWriteSource;
    private ActiveSourceIdSet? _activeSourceIdSet;

    internal RuntimeSourceTopology(StateSourceSet<TFragment> sourceSet, string modelId)
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        _sourceSet = sourceSet.WithModelId(modelId);
        _activeSources = _sourceSet.Sources.ToArray();
    }

    internal StateSourceSet<TFragment> SourceSet => _sourceSet;

    internal bool IsSingleSourceFastPath => _isSingleSourceFastPath;

    internal StateSource<TFragment>[] FastPathSources => _fastPathSources;

    internal StateSource<TFragment>? FastPathWriteSource => _fastPathWriteSource;

    /// <summary>Lock-free snapshot of the currently active sources.</summary>
    internal StateSource<TFragment>[] GetActiveSources() => Volatile.Read(ref _activeSources);

    internal StateSource<TFragment>[] GetReversedActiveSources()
    {
        var activeSources = GetActiveSources();
        var reversed = new StateSource<TFragment>[activeSources.Length];
        for (var index = 0; index < activeSources.Length; index++)
        {
            reversed[index] = activeSources[activeSources.Length - 1 - index];
        }

        return reversed;
    }

    /// <summary>The topology-change signal currently observed by watchers.</summary>
    internal Task TopologyChangedTask
    {
        get
        {
            lock (_gate)
            {
                return _sourceTopologyChanged.Task;
            }
        }
    }

    internal StateSource<TFragment> FindSource(SourceId sourceId) =>
        _sourceSet.Sources.FirstOrDefault(candidate => candidate.Id == sourceId)
        ?? throw new InvalidOperationException($"State source '{sourceId}' is not registered.");

    internal bool IsSourceActive(SourceId sourceId)
    {
        lock (_gate)
        {
            return !_retiredSourceIds.Contains(sourceId);
        }
    }

    /// <summary>
    /// Retires sources, publishing a new active snapshot and completing the previous
    /// topology signal. Returns the new active array, or <c>null</c> when nothing changed.
    /// </summary>
    internal StateSource<TFragment>[]? RetireSources(IEnumerable<SourceId> sourceIds)
    {
        TaskCompletionSource? topologyChanged = null;
        StateSource<TFragment>[]? active = null;
        lock (_gate)
        {
            var changed = false;
            foreach (var sourceId in sourceIds)
            {
                changed |= _retiredSourceIds.Add(sourceId);
            }

            if (!changed)
            {
                return null;
            }

            active = _sourceSet
                .Sources.Where(source => !_retiredSourceIds.Contains(source.Id))
                .ToArray();
            Volatile.Write(ref _activeSources, active);
            topologyChanged = _sourceTopologyChanged;
            _sourceTopologyChanged = RuntimeState.NewTopologySignal();
        }

        topologyChanged?.TrySetResult();
        return active;
    }

    /// <summary>Stable per-source key used by details snapshots.</summary>
    internal string GetDetailsSourceKey(SourceId sourceId)
    {
        lock (_gate)
        {
            if (!_detailsSourceKeys.TryGetValue(sourceId, out var key))
            {
                key = Guid.NewGuid().ToString("N");
                _detailsSourceKeys.Add(sourceId, key);
            }

            return key;
        }
    }

    /// <summary>
    /// Enables the single-file fast path (#231): one writable root source with no write
    /// routing and no migrations lets construction precompute the decisions the general
    /// multi-source machinery would otherwise re-derive per operation. Semantics are
    /// unchanged; every fast path falls back to the general implementation whenever the
    /// topology changes (for example source retirement).
    /// </summary>
    internal void TryEnableSingleSourceFastPath(StateWritePlan writePlan, int migrationCount)
    {
        var activeSources = Volatile.Read(ref _activeSources);
        if (
            activeSources.Length == 1
            && _sourceSet.Count == 1
            && migrationCount == 0
            && writePlan.PropertyRoutes.Count == 0
        )
        {
            var single = activeSources[0];
            if (
                single.Writer is not null
                && !single.ExplicitOnly
                && single.OwnedPropertyPaths.Count == 0
                && writePlan.DefaultSourceId == single.Id
            )
            {
                _isSingleSourceFastPath = true;
                _fastPathSources = activeSources;
                _fastPathWriteSource = single;
            }
        }
    }

    /// <summary>Cached active-source identity set used by watcher fan-out.</summary>
    internal HashSet<SourceId> GetActiveSourceIds(StateSource<TFragment>[] activeSources)
    {
        var cached = Volatile.Read(ref _activeSourceIdSet);
        if (cached is not null && ReferenceEquals(cached.Sources, activeSources))
        {
            return cached.Ids;
        }

#if NETSTANDARD
        var ids = new HashSet<SourceId>();
#else
        var ids = new HashSet<SourceId>(activeSources.Length);
#endif
        foreach (var source in activeSources)
        {
            ids.Add(source.Id);
        }

        Volatile.Write(ref _activeSourceIdSet, new ActiveSourceIdSet(activeSources, ids));
        return ids;
    }

    private sealed class ActiveSourceIdSet
    {
        public ActiveSourceIdSet(StateSource<TFragment>[] sources, HashSet<SourceId> ids)
        {
            Sources = sources;
            Ids = ids;
        }

        public StateSource<TFragment>[] Sources { get; }

        public HashSet<SourceId> Ids { get; }
    }
}
