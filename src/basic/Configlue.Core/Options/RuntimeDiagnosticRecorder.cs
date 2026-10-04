using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Configlue;

// Only configured sources are retained. Values are never stored; per-operation subject keys are recorded on events.
// External observability uses ILogger + ActivitySource/Meter. The compact DevTools snapshot is the only
// retained state; there is no public event bus and no retained history.
internal sealed class RuntimeDiagnosticRecorder : IConfiglueRuntimeDiagnostics
{
    private readonly object _gate = new();
    private readonly string _stateName;
    private readonly string _modelId;
    private readonly int _modelVersion;
    private readonly Func<SubjectKey>? _subjectKeyProvider;
    private readonly ConfiglueRuntimeDiagnosticOptions _options;
    private readonly ILogger? _logger;
    private readonly bool _hasLogger;
    private readonly Dictionary<SourceId, ConfiglueRuntimeSourceSnapshot> _sources;
    private readonly Dictionary<SourceId, int> _watchCounts = new();
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
        ILogger? logger = null,
        Func<SubjectKey>? subjectKeyProvider = null
    )
    {
        options.Validate();
        _stateName = stateName;
        _modelId = modelId;
        _modelVersion = modelVersion;
        _subjectKeyProvider = subjectKeyProvider;
        _options = options;
        _logger = logger;
        _hasLogger = logger is not null;
        _sources = sources.ToDictionary(static source => source.Id);
    }

    // Fast path for the fully-disabled case: only the truly-dynamic checks run per
    // operation (snapshot flag is immutable, logger presence is cached in _hasLogger,
    // telemetry listeners are dynamic). No Stopwatch/Activity work when nothing can
    // observe the outcome (issue #276 keeps this shape; issue #278 limits it to the
    // snapshot-only surface with DiagnosticOperation.IsActive keeping disabled
    // diagnostics free of async state machines and event construction).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsOperationEnabled()
    {
        if (_options.TrackSnapshot)
            return true;
        if (_hasLogger)
            return true;
        return ConfiglueTelemetry.HasObservers();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsOutcomeEnabled(ConfiglueDiagnosticEventKind kind)
    {
        if (_options.TrackSnapshot)
            return true;
        if (ConfiglueTelemetry.IsEnabled(kind))
            return true;
        return _hasLogger && LoggerIsEnabled();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool LoggerIsEnabled()
    {
        // Conservative probe over every level the diagnostic logging mapping can emit
        // (see RuntimeDiagnosticLogging.Log). The previous per-kind switch mirrored that
        // mapping so only the relevant level was probed, but the mirror was a
        // maintenance hazard: any mapping change silently dropped events here. This
        // path runs only when a logger is attached (rare next to the logger-less
        // default), so up to three extra IsEnabled probes are negligible.
        // Kept from #276 while #278 shrinks the event/history surface: do not
        // reintroduce a per-kind RuntimeDiagnosticLogging.IsEnabled probe here.
        return _logger!.IsEnabled(LogLevel.Trace)
            || _logger.IsEnabled(LogLevel.Debug)
            || _logger.IsEnabled(LogLevel.Warning)
            || _logger.IsEnabled(LogLevel.Error);
    }

    internal DiagnosticOperation Start(
        ConfiglueDiagnosticOperation operation,
        SourceId? sourceId = null
    )
    {
        if (!IsOperationEnabled() && !ConfiglueTelemetry.Activities.HasListeners())
            return default;
        var started = Stopwatch.GetTimestamp();
        Activity? activity = null;
        if (ConfiglueTelemetry.Activities.HasListeners())
        {
            activity = ConfiglueTelemetry.Activities.StartActivity(
                ConfiglueTelemetry.ActivityName(operation)
            );
            if (activity?.IsAllDataRequested == true)
            {
                activity.SetTag("configlue.state", _stateName);
                activity.SetTag("configlue.model.id", _modelId);
                activity.SetTag("configlue.model.version", _modelVersion);
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
        return new DiagnosticOperation(this, sourceId, started, activity);
    }

    internal void Record(
        ConfiglueDiagnosticEventKind kind,
        SourceId? sourceId = null,
        StateReadStatus? readStatus = null,
        bool hasRevision = false,
        TimeSpan duration = default,
        string? errorCategory = null,
        bool canceled = false,
        bool? effectiveValueChanged = null
    )
    {
        if (!IsOutcomeEnabled(kind))
            return;
        RecordCore(
            kind,
            sourceId,
            readStatus,
            hasRevision,
            duration,
            errorCategory,
            canceled,
            effectiveValueChanged
        );
    }

    // The subject scope is sampled at record time so subject-scoped operations on one state
    // instance report their own subject while sharing the instance's state-name identity.
    private void RecordCore(
        ConfiglueDiagnosticEventKind kind,
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
        var subjectKey = _subjectKeyProvider?.Invoke() ?? default;
        lock (_gate)
        {
            var sourceKind =
                sourceId is { } sourceKey && _sources.TryGetValue(sourceKey, out var source)
                    ? source.Kind
                    : null;
            diagnosticEvent = new ConfiglueDiagnosticEvent(
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
                subjectKey
            );
            if (_options.TrackSnapshot)
                UpdateSnapshot(diagnosticEvent);
        }

        ConfiglueTelemetry.Record(diagnosticEvent);
        RuntimeDiagnosticLogging.Log(_logger, diagnosticEvent);
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
        }
        _sources[sourceId] = source;
    }

    internal void NoteWatchStarted(SourceId sourceId)
    {
        lock (_gate)
        {
            if (!_sources.TryGetValue(sourceId, out var source))
                return;
            _watchCounts.TryGetValue(sourceId, out var count);
            _watchCounts[sourceId] = count + 1;
            _sources[sourceId] = source.WithWatching(true);
        }
    }

    internal void NoteWatchStopped(SourceId sourceId)
    {
        lock (_gate)
        {
            if (!_sources.TryGetValue(sourceId, out var source))
                return;
            _watchCounts.TryGetValue(sourceId, out var activeCount);
            _watchCounts[sourceId] = Math.Max(0, activeCount - 1);
            _sources[sourceId] = source.WithWatching(activeCount > 1);
        }
    }

    internal void NoteWatchSignaled(SourceId sourceId)
    {
        var timestamp = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_sources.TryGetValue(sourceId, out var source))
                return;
            _sources[sourceId] = source.WithWatchSignal(timestamp);
        }
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

    internal readonly struct DiagnosticOperation(
        RuntimeDiagnosticRecorder? owner,
        SourceId? sourceId,
        long started,
        Activity? activity
    )
    {
        internal bool IsActive => owner is not null;

        internal void Complete(
            ConfiglueDiagnosticEventKind kind,
            StateReadStatus? status = null,
            bool hasRevision = false,
            bool? effectiveValueChanged = null
        )
        {
            if (owner is null)
                return;
            if (!owner.IsOutcomeEnabled(kind))
            {
                activity?.Dispose();
                return;
            }
            owner.RecordCore(
                kind,
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
            if (owner is null)
                return;
            if (!owner.IsOutcomeEnabled(kind))
            {
                activity?.Dispose();
                return;
            }
            owner.RecordCore(
                kind,
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
