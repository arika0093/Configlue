using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Configlue;

// Only configured sources are retained. Subject/route/resource identities and values are never stored.
internal sealed class RuntimeDiagnosticRecorder : IConfiglueRuntimeDiagnostics
{
    private readonly object _gate = new();
    private readonly string _stateName;
    private readonly string _modelId;
    private readonly int _modelVersion;
    private readonly ConfiglueRuntimeDiagnosticOptions _options;
    private readonly ILogger? _logger;
    private readonly bool _configEnabled;
    private readonly bool _hasLogger;
    private readonly ConfiglueDiagnosticEvent[] _history;
    private readonly Dictionary<SourceId, ConfiglueRuntimeSourceSnapshot> _sources;
    private readonly Dictionary<SourceId, int> _watchCounts = new();
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
        IEnumerable<ConfiglueRuntimeSourceSnapshot> sources,
        ILogger? logger = null
    )
    {
        options.Validate();
        _stateName = stateName;
        _modelId = modelId;
        _modelVersion = modelVersion;
        _options = options;
        _logger = logger;
        _history = new ConfiglueDiagnosticEvent[options.EventHistoryCapacity];
        _sources = sources.ToDictionary(static source => source.Id);
        // Immutable configuration evaluated once. TrackSnapshot and history capacity never
        // change after construction, and logger presence is fixed. Only explicit listener
        // subscriptions and Activity/Meter listeners can change dynamically and must be
        // re-evaluated per operation.
        _configEnabled = options.TrackSnapshot || _history.Length != 0;
        _hasLogger = logger is not null;
    }

    // Fast path for the fully-disabled case: one cached static branch plus only the
    // truly-dynamic checks (explicit listeners, Activity/Meter listeners). Logger checks
    // are hoisted behind _hasLogger so the common logger-less path performs no virtual
    // calls, and when a logger is present only the level relevant to the event kind is
    // probed instead of all four levels.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsEnabled(ConfiglueDiagnosticEventKind kind)
    {
        if (_configEnabled)
            return true;
        if (Volatile.Read(ref _listeners).Length != 0)
            return true;
        if (ConfiglueTelemetry.IsEnabled(kind))
            return true;
        return _hasLogger && LoggerIsEnabled(kind);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool LoggerIsEnabled(ConfiglueDiagnosticEventKind kind)
    {
        // Mirrors RuntimeDiagnosticLogging.Log level mapping. Checking only the relevant
        // level is safe: when configuration, listeners, and telemetry are all disabled,
        // logging is the sole observable effect, and kinds mapped to other levels would
        // not emit anyway.
        var level = kind switch
        {
            ConfiglueDiagnosticEventKind.ResolveStarted
            or ConfiglueDiagnosticEventKind.SourceReadStarted
            or ConfiglueDiagnosticEventKind.WriteStarted
            or ConfiglueDiagnosticEventKind.ReloadStarted
            or ConfiglueDiagnosticEventKind.MigrationStarted
            or ConfiglueDiagnosticEventKind.WatchStarted
            or ConfiglueDiagnosticEventKind.WatchStopped => LogLevel.Trace,
            ConfiglueDiagnosticEventKind.WriteConflict
            or ConfiglueDiagnosticEventKind.ValidationFailed => LogLevel.Warning,
            ConfiglueDiagnosticEventKind.ResolveFailed
            or ConfiglueDiagnosticEventKind.SourceReadFailed
            or ConfiglueDiagnosticEventKind.WriteFailed
            or ConfiglueDiagnosticEventKind.ReloadFailed
            or ConfiglueDiagnosticEventKind.MigrationFailed
            or ConfiglueDiagnosticEventKind.ObserverFailed => LogLevel.Error,
            ConfiglueDiagnosticEventKind.SourceFallback => LogLevel.Debug,
            _ => LogLevel.Debug,
        };
        if (kind == ConfiglueDiagnosticEventKind.SourceFallback)
        {
            // Fallback maps to Warning when unavailable, Debug otherwise; probe both.
            // This path is cold (multi-source miss only).
            return _logger!.IsEnabled(LogLevel.Debug) || _logger.IsEnabled(LogLevel.Warning);
        }
        return _logger!.IsEnabled(level);
    }

    internal DiagnosticOperation Start(
        ConfiglueDiagnosticEventKind kind,
        SourceId? sourceId = null,
        long parentOperationId = 0
    )
    {
        if (!IsEnabled(kind))
            return default;
        var operationId = Interlocked.Increment(ref _nextOperationId);
        var started = Stopwatch.GetTimestamp();
        Activity? activity = null;
        if (
            ConfiglueTelemetry.Activities.HasListeners()
            && ConfiglueTelemetry.ActivityName(kind) is { } activityName
        )
        {
            activity = ConfiglueTelemetry.Activities.StartActivity(activityName);
            if (activity?.IsAllDataRequested == true)
            {
                activity.SetTag("configlue.state", _stateName);
                activity.SetTag("configlue.model.id", _modelId);
                activity.SetTag("configlue.model.version", _modelVersion);
                activity.SetTag("configlue.operation.id", operationId);
                if (sourceId is { } sourceKey)
                {
                    activity.SetTag("configlue.source.id", sourceKey.Value);
                    lock (_gate)
                    {
                        if (_sources.TryGetValue(sourceKey, out var source))
                            activity.SetTag("configlue.source.kind", source.Kind);
                    }
                }
            }
        }
        RecordCore(kind, operationId, parentOperationId, sourceId);
        return new DiagnosticOperation(
            this,
            operationId,
            parentOperationId,
            sourceId,
            started,
            activity
        );
    }

    internal void Record(
        ConfiglueDiagnosticEventKind kind,
        long operationId = 0,
        long parentOperationId = 0,
        SourceId? sourceId = null,
        StateReadStatus? readStatus = null,
        bool hasRevision = false,
        TimeSpan duration = default,
        string? errorCategory = null,
        bool canceled = false,
        bool? effectiveValueChanged = null
    )
    {
        if (!IsEnabled(kind))
            return;
        RecordCore(
            kind,
            operationId,
            parentOperationId,
            sourceId,
            readStatus,
            hasRevision,
            duration,
            errorCategory,
            canceled,
            effectiveValueChanged
        );
    }

    // Enabled implementation separated from the always-disabled fast path above so the
    // disabled case returns before any event construction, locking, or observer dispatch.
    private void RecordCore(
        ConfiglueDiagnosticEventKind kind,
        long operationId = 0,
        long parentOperationId = 0,
        SourceId? sourceId = null,
        StateReadStatus? readStatus = null,
        bool hasRevision = false,
        TimeSpan duration = default,
        string? errorCategory = null,
        bool canceled = false,
        bool? effectiveValueChanged = null
    )
    {
        ConfiglueDiagnosticEvent diagnosticEvent;
        lock (_gate)
        {
            var sourceKind =
                sourceId is { } sourceKey && _sources.TryGetValue(sourceKey, out var source)
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
                effectiveValueChanged,
                Activity.Current?.TraceId.ToString()
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

        ConfiglueTelemetry.Record(diagnosticEvent);
        RuntimeDiagnosticLogging.Log(_logger, diagnosticEvent);

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
                source = source.WithRead(diagnosticEvent);
                break;
            case ConfiglueDiagnosticEventKind.WatchStarted:
                _watchCounts.TryGetValue(sourceId, out var count);
                _watchCounts[sourceId] = count + 1;
                source = source.WithWatching(true);
                break;
            case ConfiglueDiagnosticEventKind.WatchStopped:
                _watchCounts.TryGetValue(sourceId, out var activeCount);
                _watchCounts[sourceId] = Math.Max(0, activeCount - 1);
                source = source.WithWatching(activeCount > 1);
                break;
            case ConfiglueDiagnosticEventKind.WatchSignaled:
                source = source.WithWatchSignal(diagnosticEvent.Timestamp);
                break;
        }
        _sources[sourceId] = source;
    }

    internal void SetActiveSources(IEnumerable<SourceId> activeSourceIds)
    {
        lock (_gate)
        {
            var ids = new HashSet<SourceId>(activeSourceIds);
            foreach (var id in _sources.Keys.ToArray())
                _sources[id] = _sources[id].WithActive(ids.Contains(id));
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
        SourceId? sourceId,
        long started,
        Activity? activity
    )
    {
        internal long Id { get; } = operationId;

        internal void Complete(
            ConfiglueDiagnosticEventKind kind,
            StateReadStatus? status = null,
            bool hasRevision = false,
            bool? effectiveValueChanged = null
        )
        {
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
            if (activity?.IsAllDataRequested == true)
            {
                activity.SetTag("configlue.has_revision", hasRevision);
                if (status is { } readStatus)
                    activity.SetTag("configlue.read.status", readStatus.ToString());
                if (effectiveValueChanged is { } changed)
                    activity.SetTag("configlue.effective_value_changed", changed);
                if (kind == ConfiglueDiagnosticEventKind.ReloadFailed)
                    activity.SetStatus(ActivityStatusCode.Error);
                if (kind == ConfiglueDiagnosticEventKind.WriteCompleted)
                    activity.SetTag("configlue.write.result", "success");
            }
            activity?.Dispose();
        }

        internal void Fail(
            ConfiglueDiagnosticEventKind kind,
            Exception exception,
            bool canceled = false
        )
        {
            owner?.Record(
                kind,
                Id,
                parentOperationId,
                sourceId,
                duration: Elapsed(),
                errorCategory: exception.GetType().FullName,
                canceled: canceled
            );
            if (activity?.IsAllDataRequested == true)
            {
                activity.SetTag("configlue.canceled", canceled);
                activity.SetTag("error.type", exception.GetType().FullName);
                if (
                    kind
                    is ConfiglueDiagnosticEventKind.WriteFailed
                        or ConfiglueDiagnosticEventKind.WriteConflict
                )
                    activity.SetTag(
                        "configlue.write.result",
                        kind == ConfiglueDiagnosticEventKind.WriteConflict ? "conflict" : "failed"
                    );
                if (!canceled)
                    activity.SetStatus(ActivityStatusCode.Error);
            }
            activity?.Dispose();
        }

        private TimeSpan Elapsed() =>
            TimeSpan.FromSeconds(
                (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency
            );
    }
}
