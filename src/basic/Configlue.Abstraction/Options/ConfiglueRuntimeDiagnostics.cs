namespace Configlue;

/// <summary>The runtime operation represented by a value-free diagnostic event.</summary>
/// <remarks>
/// Only terminal outcomes and value-free notifications are public. Operation starts,
/// watch lifecycle, and correlation are covered by <c>ILogger</c>,
/// <c>ConfiglueTelemetry.ActivitySourceName</c>, and <c>ConfiglueTelemetry.MeterName</c>;
/// the compact <see cref="ConfiglueRuntimeDiagnosticSnapshot"/> covers DevTools status.
/// Advanced observability vocabulary.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public enum ConfiglueDiagnosticEventKind
{
    /// <summary>A resolution finished.</summary>
    ResolveCompleted,

    /// <summary>A resolution failed.</summary>
    ResolveFailed,

    /// <summary>A source read finished.</summary>
    SourceReadCompleted,

    /// <summary>A source read failed.</summary>
    SourceReadFailed,

    /// <summary>Resolution continued after an unsuccessful source read.</summary>
    SourceFallback,

    /// <summary>A reload finished.</summary>
    ReloadCompleted,

    /// <summary>A reload failed.</summary>
    ReloadFailed,

    /// <summary>The effective value changed, without including that value.</summary>
    EffectiveValueChanged,

    /// <summary>A physical write finished.</summary>
    WriteCompleted,

    /// <summary>A write conflicted with another revision.</summary>
    WriteConflict,

    /// <summary>A write failed.</summary>
    WriteFailed,

    /// <summary>Validation rejected a configuration contribution or effective model.</summary>
    ValidationFailed,

    /// <summary>A schema migration finished.</summary>
    MigrationCompleted,

    /// <summary>A schema migration failed.</summary>
    MigrationFailed,

    /// <summary>An application notification callback failed; its exception message is not included.</summary>
    ObserverFailed,
}

/// <summary>A structured runtime event that never contains model or fragment values.</summary>
/// <remarks>
/// Runtime-created immutable value snapshot. Equality compares all currently exposed fields; no positional
/// constructor or deconstruction contract is provided so new diagnostics can be added compatibly.
/// Operation correlation and tracing use <c>System.Diagnostics.Activity.Current</c> instead of
/// event identifiers. Advanced observability vocabulary.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public readonly record struct ConfiglueDiagnosticEvent
{
    internal ConfiglueDiagnosticEvent(
        DateTimeOffset timestamp,
        ConfiglueDiagnosticEventKind kind,
        string stateName,
        string modelId,
        int modelVersion,
        SourceId? sourceId,
        string? sourceKind,
        StateReadStatus? readStatus,
        bool hasRevision,
        TimeSpan duration,
        string? errorCategory,
        bool canceled,
        bool? effectiveValueChanged,
        SubjectKey subjectKey = default
    )
    {
        Timestamp = timestamp;
        Kind = kind;
        StateName = stateName;
        ModelId = modelId;
        ModelVersion = modelVersion;
        SourceId = sourceId;
        SourceKind = sourceKind;
        ReadStatus = readStatus;
        HasRevision = hasRevision;
        Duration = duration;
        ErrorCategory = errorCategory;
        Canceled = canceled;
        EffectiveValueChanged = effectiveValueChanged;
        SubjectKey = subjectKey;
    }

    /// <summary>The time the event was recorded.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>The runtime event kind.</summary>
    public ConfiglueDiagnosticEventKind Kind { get; }

    /// <summary>The state-name identity of the state instance that recorded this event.</summary>
    public string StateName { get; }

    /// <summary>The subject scope of the operation, or the default key for server-wide operations.</summary>
    /// <remarks>Subject identity is reported separately from source and physical resource identity.</remarks>
    public SubjectKey SubjectKey { get; }

    /// <summary>The model schema identifier.</summary>
    public string ModelId { get; }

    /// <summary>The model schema version.</summary>
    public int ModelVersion { get; }

    /// <summary>The logical Configlue source registration identifier, when applicable.</summary>
    public SourceId? SourceId { get; }

    /// <summary>The source reader or writer type, when applicable.</summary>
    public string? SourceKind { get; }

    /// <summary>The observed read status, when available.</summary>
    public StateReadStatus? ReadStatus { get; }

    /// <summary>Whether the operation observed a revision; the revision is not included.</summary>
    public bool HasRevision { get; }

    /// <summary>Elapsed operation time, or zero for a notification.</summary>
    public TimeSpan Duration { get; }

    /// <summary>The exception type name, never its message or stack trace.</summary>
    public string? ErrorCategory { get; }

    /// <summary>Whether the operation was canceled by its caller.</summary>
    public bool Canceled { get; }

    /// <summary>Whether a reload changed the effective model.</summary>
    public bool? EffectiveValueChanged { get; }
}

/// <summary>The last observed state of a configured source, without performing a source read.</summary>
/// <remarks>Runtime-created immutable value snapshot with value equality and no positional deconstruction contract.
/// Advanced observability vocabulary.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public readonly record struct ConfiglueRuntimeSourceSnapshot
{
    internal ConfiglueRuntimeSourceSnapshot(
        SourceId id,
        string kind,
        bool isActive,
        bool canRead,
        bool canWrite,
        bool canWatch,
        bool isWatching,
        ConfiglueDiagnosticEvent? lastRead,
        DateTimeOffset? lastSuccessfulRead,
        DateTimeOffset? lastWatchSignal
    )
    {
        Id = id;
        Kind = kind;
        IsActive = isActive;
        CanRead = canRead;
        CanWrite = canWrite;
        CanWatch = canWatch;
        IsWatching = isWatching;
        LastRead = lastRead;
        LastSuccessfulRead = lastSuccessfulRead;
        LastWatchSignal = lastWatchSignal;
    }

    /// <summary>The configured source identifier.</summary>
    public SourceId Id { get; }

    /// <summary>The configured reader type name.</summary>
    public string Kind { get; }

    /// <summary>Whether this source is active in the topology.</summary>
    public bool IsActive { get; }

    /// <summary>Whether this source supports reads.</summary>
    public bool CanRead { get; }

    /// <summary>Whether this source supports writes.</summary>
    public bool CanWrite { get; }

    /// <summary>Whether this source supports watching.</summary>
    public bool CanWatch { get; }

    /// <summary>Whether a watch wait is currently active.</summary>
    public bool IsWatching { get; }

    /// <summary>The last completed or failed physical read.</summary>
    public ConfiglueDiagnosticEvent? LastRead { get; }

    /// <summary>The last successful read timestamp.</summary>
    public DateTimeOffset? LastSuccessfulRead { get; }

    /// <summary>The last observed watch signal timestamp.</summary>
    public DateTimeOffset? LastWatchSignal { get; }

    internal ConfiglueRuntimeSourceSnapshot WithRead(ConfiglueDiagnosticEvent diagnosticEvent) =>
        new(
            Id,
            Kind,
            IsActive,
            CanRead,
            CanWrite,
            CanWatch,
            IsWatching,
            diagnosticEvent,
            diagnosticEvent.ReadStatus == StateReadStatus.Success
                ? diagnosticEvent.Timestamp
                : LastSuccessfulRead,
            LastWatchSignal
        );

    internal ConfiglueRuntimeSourceSnapshot WithWatching(bool isWatching) =>
        new(
            Id,
            Kind,
            IsActive,
            CanRead,
            CanWrite,
            CanWatch,
            isWatching,
            LastRead,
            LastSuccessfulRead,
            LastWatchSignal
        );

    internal ConfiglueRuntimeSourceSnapshot WithWatchSignal(DateTimeOffset timestamp) =>
        new(
            Id,
            Kind,
            IsActive,
            CanRead,
            CanWrite,
            CanWatch,
            IsWatching,
            LastRead,
            LastSuccessfulRead,
            timestamp
        );

    internal ConfiglueRuntimeSourceSnapshot WithActive(bool isActive) =>
        new(
            Id,
            Kind,
            isActive,
            CanRead,
            CanWrite,
            CanWatch,
            IsWatching,
            LastRead,
            LastSuccessfulRead,
            LastWatchSignal
        );
}

/// <summary>An immutable, I/O-free copy of the last observed runtime diagnostics.</summary>
/// <remarks>
/// Compact DevTools status: the last observed outcomes per operation area plus per-source
/// last-read/watch state. Detailed timelines belong to <c>ILogger</c> sinks and
/// <c>System.Diagnostics</c> listeners, not to this snapshot.
/// Advanced observability vocabulary.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class ConfiglueRuntimeDiagnosticSnapshot
{
    /// <summary>Creates a runtime snapshot using already observed diagnostic data.</summary>
    internal ConfiglueRuntimeDiagnosticSnapshot(
        string stateName,
        string modelId,
        int modelVersion,
        IEnumerable<ConfiglueRuntimeSourceSnapshot> sources,
        ConfiglueDiagnosticEvent? lastResolution,
        ConfiglueDiagnosticEvent? lastReload,
        ConfiglueDiagnosticEvent? lastWrite,
        ConfiglueDiagnosticEvent? lastMigration
    )
    {
        ArgumentNullException.ThrowIfNull(stateName);
        ArgumentNullException.ThrowIfNull(modelId);
        ArgumentNullException.ThrowIfNull(sources);
        StateName = stateName;
        ModelId = modelId;
        ModelVersion = modelVersion;
        Sources = Array.AsReadOnly(sources.ToArray());
        LastResolution = lastResolution;
        LastReload = lastReload;
        LastWrite = lastWrite;
        LastMigration = lastMigration;
    }

    /// <summary>The state-name identity of the state instance described by this snapshot.</summary>
    /// <remarks>Subject identity is per-operation and is reported by events, details resolutions, and write receipts.</remarks>
    public string StateName { get; }

    /// <summary>The generated schema identifier.</summary>
    public string ModelId { get; }

    /// <summary>The generated schema version.</summary>
    public int ModelVersion { get; }

    /// <summary>Configured sources with their last observed operations.</summary>
    public IReadOnlyList<ConfiglueRuntimeSourceSnapshot> Sources { get; }

    /// <summary>The last completed or failed resolution.</summary>
    public ConfiglueDiagnosticEvent? LastResolution { get; }

    /// <summary>The last completed or failed reload.</summary>
    public ConfiglueDiagnosticEvent? LastReload { get; }

    /// <summary>The last completed, conflicted, or failed write.</summary>
    public ConfiglueDiagnosticEvent? LastWrite { get; }

    /// <summary>The last completed or failed migration.</summary>
    public ConfiglueDiagnosticEvent? LastMigration { get; }
}

/// <summary>Provides cached runtime diagnostics independently of a model's value API.</summary>
/// <remarks>
/// Compact DevTools status surface. External observability uses <c>ILogger</c> plus
/// <c>System.Diagnostics.ActivitySource</c>/<c>Meter</c> (<c>ConfiglueTelemetry</c>);
/// this interface only exposes the cached snapshot, never an event bus or history.
/// Advanced observability SPI.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueRuntimeDiagnostics
{
    /// <summary>Copies already observed data without reading, reloading, or locking a backend.</summary>
    ConfiglueRuntimeDiagnosticSnapshot GetRuntimeSnapshot();
}

/// <summary>Accesses optional runtime diagnostics through the existing typed diagnostics service.</summary>
/// <remarks>Advanced observability helpers.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public static class ConfiglueRuntimeDiagnosticExtensions
{
    /// <summary>Gets a cached snapshot without causing configuration I/O.</summary>
    public static ConfiglueRuntimeDiagnosticSnapshot GetRuntimeSnapshot<T>(
        this IConfiglueDiagnostics<T> diagnostics
    ) => GetProvider(diagnostics).GetRuntimeSnapshot();

    private static IConfiglueRuntimeDiagnostics GetProvider<T>(IConfiglueDiagnostics<T> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return diagnostics as IConfiglueRuntimeDiagnostics
            ?? throw new NotSupportedException(
                "This diagnostics implementation does not expose runtime diagnostics."
            );
    }
}
