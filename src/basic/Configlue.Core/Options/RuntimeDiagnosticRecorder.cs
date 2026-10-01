using System.Diagnostics;

namespace Configlue;

// Only configured sources are retained. Subject/route/resource identities and values are never stored.
internal sealed class RuntimeDiagnosticRecorder : IConfiglueRuntimeDiagnostics
{
    private readonly object _gate = new();
    private readonly string _stateName;
    private readonly string _modelId;
    private readonly int _modelVersion;
    private readonly ConfiglueRuntimeDiagnosticOptions _options;
    private readonly ConfiglueDiagnosticEvent[] _history;
    private readonly Dictionary<string, ConfiglueRuntimeSourceSnapshot> _sources;
    private Action<ConfiglueDiagnosticEvent>[] _listeners = [];
    private long _sequence;
    private long _nextOperationId;
    private int _historyCount;
    private int _historyNext;
    private ConfiglueDiagnosticEvent? _lastResolution;
    private ConfiglueDiagnosticEvent? _lastReload;
    private ConfiglueDiagnosticEvent? _lastWrite;
    private ConfiglueDiagnosticEvent? _lastMigration;

    internal RuntimeDiagnosticRecorder(
        string stateName,
        string modelId,
        int modelVersion,
        ConfiglueRuntimeDiagnosticOptions options,
        IEnumerable<ConfiglueRuntimeSourceSnapshot> sources
    )
    {
        options.Validate();
        _stateName = stateName;
        _modelId = modelId;
        _modelVersion = modelVersion;
        _options = options;
        _history = new ConfiglueDiagnosticEvent[options.EventHistoryCapacity];
        _sources = sources.ToDictionary(static source => source.Id, StringComparer.Ordinal);
    }

    internal bool IsEnabled =>
        _options.TrackSnapshot || _history.Length != 0 || Volatile.Read(ref _listeners).Length != 0;

    internal DiagnosticOperation Start(
        ConfiglueDiagnosticEventKind kind,
        string? sourceId = null,
        long parentOperationId = 0
    )
    {
        if (!IsEnabled)
            return default;
        var operationId = Interlocked.Increment(ref _nextOperationId);
        var started = Stopwatch.GetTimestamp();
        Record(kind, operationId, parentOperationId, sourceId);
        return new DiagnosticOperation(this, operationId, parentOperationId, sourceId, started);
    }

    internal void Record(
        ConfiglueDiagnosticEventKind kind,
        long operationId = 0,
        long parentOperationId = 0,
        string? sourceId = null,
        StateReadStatus? readStatus = null,
        bool hasRevision = false,
        TimeSpan duration = default,
        string? errorCategory = null,
        bool canceled = false,
        bool? effectiveValueChanged = null
    )
    {
        if (!IsEnabled)
            return;
        ConfiglueDiagnosticEvent diagnosticEvent;
        lock (_gate)
        {
            var sourceKind =
                sourceId is not null && _sources.TryGetValue(sourceId, out var source)
                    ? source.Kind
                    : null;
            diagnosticEvent = new ConfiglueDiagnosticEvent(
                ++_sequence,
                operationId,
                parentOperationId,
                DateTimeOffset.UtcNow,
                kind,
                _stateName,
                _modelId,
                _modelVersion,
                sourceId,
                sourceKind,
                readStatus,
                hasRevision,
                duration,
                errorCategory,
                canceled,
                effectiveValueChanged
            );
            if (_options.TrackSnapshot)
                UpdateSnapshot(diagnosticEvent);
            if (_history.Length != 0)
            {
                _history[_historyNext] = diagnosticEvent;
                _historyNext = (_historyNext + 1) % _history.Length;
                _historyCount = Math.Min(_historyCount + 1, _history.Length);
            }
        }

        foreach (var listener in Volatile.Read(ref _listeners))
        {
            try
            {
                listener(diagnosticEvent);
            }
            catch (Exception)
            {
                // Observers cannot turn a successful source operation into a failure.
            }
        }
    }

    private void UpdateSnapshot(ConfiglueDiagnosticEvent diagnosticEvent)
    {
        switch (diagnosticEvent.Kind)
        {
            case ConfiglueDiagnosticEventKind.ResolveCompleted:
            case ConfiglueDiagnosticEventKind.ResolveFailed:
                _lastResolution = diagnosticEvent;
                break;
            case ConfiglueDiagnosticEventKind.ReloadCompleted:
            case ConfiglueDiagnosticEventKind.ReloadFailed:
                _lastReload = diagnosticEvent;
                break;
            case ConfiglueDiagnosticEventKind.WriteCompleted:
            case ConfiglueDiagnosticEventKind.WriteConflict:
            case ConfiglueDiagnosticEventKind.WriteFailed:
                _lastWrite = diagnosticEvent;
                break;
            case ConfiglueDiagnosticEventKind.MigrationCompleted:
            case ConfiglueDiagnosticEventKind.MigrationFailed:
                _lastMigration = diagnosticEvent;
                break;
        }
        if (
            diagnosticEvent.SourceId is not { } sourceId
            || !_sources.TryGetValue(sourceId, out var source)
        )
            return;
        switch (diagnosticEvent.Kind)
        {
            case ConfiglueDiagnosticEventKind.SourceReadCompleted:
            case ConfiglueDiagnosticEventKind.SourceReadFailed:
                source = source with
                {
                    LastRead = diagnosticEvent,
                    LastSuccessfulRead =
                        diagnosticEvent.ReadStatus == StateReadStatus.Success
                            ? diagnosticEvent.Timestamp
                            : source.LastSuccessfulRead,
                };
                break;
            case ConfiglueDiagnosticEventKind.WatchStarted:
                source = source with { IsWatching = true };
                break;
            case ConfiglueDiagnosticEventKind.WatchStopped:
                source = source with { IsWatching = false };
                break;
            case ConfiglueDiagnosticEventKind.WatchSignaled:
                source = source with { LastWatchSignal = diagnosticEvent.Timestamp };
                break;
        }
        _sources[sourceId] = source;
    }

    internal void SetActiveSources(IEnumerable<string> activeSourceIds)
    {
        lock (_gate)
        {
            var ids = new HashSet<string>(activeSourceIds, StringComparer.Ordinal);
            foreach (var id in _sources.Keys.ToArray())
                _sources[id] = _sources[id] with { IsActive = ids.Contains(id) };
        }
    }

    public ConfiglueRuntimeDiagnosticSnapshot GetRuntimeSnapshot()
    {
        lock (_gate)
        {
            return new ConfiglueRuntimeDiagnosticSnapshot(
                _stateName,
                _modelId,
                _modelVersion,
                _sources.Values,
                _lastResolution,
                _lastReload,
                _lastWrite,
                _lastMigration
            );
        }
    }

    public IReadOnlyList<ConfiglueDiagnosticEvent> GetRecentEvents()
    {
        lock (_gate)
        {
            var copy = new ConfiglueDiagnosticEvent[_historyCount];
            var first =
                (_historyNext - _historyCount + _history.Length) % Math.Max(1, _history.Length);
            for (var index = 0; index < copy.Length; index++)
                copy[index] = _history[(first + index) % _history.Length];
            return Array.AsReadOnly(copy);
        }
    }

    public IDisposable OnDiagnosticEvent(Action<ConfiglueDiagnosticEvent> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_gate)
            Volatile.Write(ref _listeners, [.. _listeners, listener]);
        return new Subscription(this, listener);
    }

    internal void ClearListeners()
    {
        lock (_gate)
            Volatile.Write(ref _listeners, []);
    }

    private sealed class Subscription(
        RuntimeDiagnosticRecorder owner,
        Action<ConfiglueDiagnosticEvent> listener
    ) : IDisposable
    {
        private RuntimeDiagnosticRecorder? _owner = owner;

        public void Dispose()
        {
            var recorder = Interlocked.Exchange(ref _owner, null);
            if (recorder is null)
                return;
            lock (recorder._gate)
            {
                var index = Array.IndexOf(recorder._listeners, listener);
                if (index < 0)
                    return;
                var listeners = new Action<ConfiglueDiagnosticEvent>[
                    recorder._listeners.Length - 1
                ];
                Array.Copy(recorder._listeners, 0, listeners, 0, index);
                Array.Copy(
                    recorder._listeners,
                    index + 1,
                    listeners,
                    index,
                    listeners.Length - index
                );
                Volatile.Write(ref recorder._listeners, listeners);
            }
        }
    }

    internal readonly struct DiagnosticOperation(
        RuntimeDiagnosticRecorder owner,
        long operationId,
        long parentOperationId,
        string? sourceId,
        long started
    )
    {
        internal long Id { get; } = operationId;

        internal void Complete(
            ConfiglueDiagnosticEventKind kind,
            StateReadStatus? status = null,
            bool hasRevision = false,
            bool? effectiveValueChanged = null
        ) =>
            owner?.Record(
                kind,
                Id,
                parentOperationId,
                sourceId,
                status,
                hasRevision,
                Elapsed(),
                effectiveValueChanged: effectiveValueChanged
            );

        internal void Fail(
            ConfiglueDiagnosticEventKind kind,
            Exception exception,
            bool canceled = false
        ) =>
            owner?.Record(
                kind,
                Id,
                parentOperationId,
                sourceId,
                duration: Elapsed(),
                errorCategory: exception.GetType().FullName,
                canceled: canceled
            );

        private TimeSpan Elapsed() =>
            TimeSpan.FromSeconds(
                (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency
            );
    }
}
