namespace Configlue;

/// <summary>The runtime operation represented by a value-free diagnostic event.</summary>
public enum ConfiglueDiagnosticEventKind
{
    /// <summary>A resolution began.</summary>
    ResolveStarted,

    /// <summary>A resolution finished.</summary>
    ResolveCompleted,

    /// <summary>A resolution failed.</summary>
    ResolveFailed,

    /// <summary>A source read began.</summary>
    SourceReadStarted,

    /// <summary>A source read finished.</summary>
    SourceReadCompleted,

    /// <summary>A source read failed.</summary>
    SourceReadFailed,

    /// <summary>Resolution continued after an unsuccessful source read.</summary>
    SourceFallback,

    /// <summary>A source watch began.</summary>
    WatchStarted,

    /// <summary>A source watch stopped.</summary>
    WatchStopped,

    /// <summary>A source reported a change.</summary>
    WatchSignaled,

    /// <summary>A reload began.</summary>
    ReloadStarted,

    /// <summary>A reload finished.</summary>
    ReloadCompleted,

    /// <summary>A reload failed.</summary>
    ReloadFailed,

    /// <summary>The effective value changed, without including that value.</summary>
    EffectiveValueChanged,

    /// <summary>A physical write began.</summary>
    WriteStarted,

    /// <summary>A physical write finished.</summary>
    WriteCompleted,

    /// <summary>A write conflicted with another revision.</summary>
    WriteConflict,

    /// <summary>A write failed.</summary>
    WriteFailed,

    /// <summary>Validation rejected a configuration contribution or effective model.</summary>
    ValidationFailed,

    /// <summary>A schema migration began.</summary>
    MigrationStarted,

    /// <summary>A schema migration finished.</summary>
    MigrationCompleted,

    /// <summary>A schema migration failed.</summary>
    MigrationFailed,

    /// <summary>An application notification callback failed; its exception message is not included.</summary>
    ObserverFailed,
}

/// <summary>A structured runtime event that never contains model or fragment values.</summary>
/// <param name="Sequence">The monotonically increasing sequence within one runtime.</param>
/// <param name="OperationId">The operation identifier within one runtime.</param>
/// <param name="ParentOperationId">The enclosing operation, or zero when absent.</param>
/// <param name="Timestamp">The time the event was recorded.</param>
/// <param name="Kind">The runtime event kind.</param>
/// <param name="StateName">The configured state name.</param>
/// <param name="ModelId">The model schema identifier.</param>
/// <param name="ModelVersion">The model schema version.</param>
/// <param name="SourceId">The logical Configlue source registration identifier, when applicable.</param>
/// <param name="SourceKind">The source reader or writer type, when applicable.</param>
/// <param name="ReadStatus">The observed read status, when available.</param>
/// <param name="HasRevision">Whether the operation observed a revision; the revision is not included.</param>
/// <param name="Duration">Elapsed operation time, or zero for a notification.</param>
/// <param name="ErrorCategory">The exception type name, never its message or stack trace.</param>
/// <param name="Canceled">Whether the operation was canceled by its caller.</param>
/// <param name="EffectiveValueChanged">Whether a reload changed the effective model.</param>
/// <param name="TraceId">The current distributed trace identifier, when a trace is active.</param>
public readonly record struct ConfiglueDiagnosticEvent(
    long Sequence,
    long OperationId,
    long ParentOperationId,
    DateTimeOffset Timestamp,
    ConfiglueDiagnosticEventKind Kind,
    string StateName,
    string ModelId,
    int ModelVersion,
    string? SourceId,
    string? SourceKind,
    StateReadStatus? ReadStatus,
    bool HasRevision,
    TimeSpan Duration,
    string? ErrorCategory,
    bool Canceled,
    bool? EffectiveValueChanged,
    string? TraceId = null
);

/// <summary>The last observed state of a configured source, without performing a source read.</summary>
/// <param name="Id">The configured source identifier.</param>
/// <param name="Kind">The configured reader type name.</param>
/// <param name="IsActive">Whether this source is active in the topology.</param>
/// <param name="CanRead">Whether this source supports reads.</param>
/// <param name="CanWrite">Whether this source supports writes.</param>
/// <param name="CanWatch">Whether this source supports watching.</param>
/// <param name="IsWatching">Whether a watch wait is currently active.</param>
/// <param name="LastRead">The last completed or failed physical read.</param>
/// <param name="LastSuccessfulRead">The last successful read timestamp.</param>
/// <param name="LastWatchSignal">The last observed watch signal timestamp.</param>
public readonly record struct ConfiglueRuntimeSourceSnapshot(
    string Id,
    string Kind,
    bool IsActive,
    bool CanRead,
    bool CanWrite,
    bool CanWatch,
    bool IsWatching,
    ConfiglueDiagnosticEvent? LastRead,
    DateTimeOffset? LastSuccessfulRead,
    DateTimeOffset? LastWatchSignal
);

/// <summary>An immutable, I/O-free copy of the last observed runtime diagnostics.</summary>
public sealed class ConfiglueRuntimeDiagnosticSnapshot
{
    /// <summary>Creates a runtime snapshot using already observed diagnostic data.</summary>
    public ConfiglueRuntimeDiagnosticSnapshot(
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

    /// <summary>The configured state name.</summary>
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
public interface IConfiglueRuntimeDiagnostics
{
    /// <summary>Copies already observed data without reading, reloading, or locking a backend.</summary>
    ConfiglueRuntimeDiagnosticSnapshot GetRuntimeSnapshot();

    /// <summary>Returns the retained event history in ascending sequence order.</summary>
    IReadOnlyList<ConfiglueDiagnosticEvent> GetRecentEvents();

    /// <summary>Subscribes to future events without starting reads or watchers.</summary>
    /// <remarks>Callbacks may run concurrently. Keep callbacks fast; callback failures are isolated from runtime operations.</remarks>
    IDisposable OnDiagnosticEvent(Action<ConfiglueDiagnosticEvent> listener);
}

/// <summary>Accesses optional runtime diagnostics through the existing typed diagnostics service.</summary>
public static class ConfiglueRuntimeDiagnosticExtensions
{
    /// <summary>Gets a cached snapshot without causing configuration I/O.</summary>
    public static ConfiglueRuntimeDiagnosticSnapshot GetRuntimeSnapshot<T>(
        this IConfiglueDiagnostics<T> diagnostics
    ) => GetProvider(diagnostics).GetRuntimeSnapshot();

    /// <summary>Gets the bounded history retained by a runtime.</summary>
    public static IReadOnlyList<ConfiglueDiagnosticEvent> GetRecentEvents<T>(
        this IConfiglueDiagnostics<T> diagnostics
    ) => GetProvider(diagnostics).GetRecentEvents();

    /// <summary>Subscribes to future runtime events without initiating configuration I/O.</summary>
    public static IDisposable OnDiagnosticEvent<T>(
        this IConfiglueDiagnostics<T> diagnostics,
        Action<ConfiglueDiagnosticEvent> listener
    ) => GetProvider(diagnostics).OnDiagnosticEvent(listener);

    private static IConfiglueRuntimeDiagnostics GetProvider<T>(IConfiglueDiagnostics<T> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return diagnostics as IConfiglueRuntimeDiagnostics
            ?? throw new NotSupportedException(
                "This diagnostics implementation does not expose runtime diagnostics."
            );
    }
}
