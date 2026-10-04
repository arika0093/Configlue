using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Configlue;

/// <summary>Identifies Configlue's SDK-independent distributed tracing and metrics instrumentation.</summary>
/// <remarks>Advanced observability identifiers.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public static class ConfiglueTelemetry
{
    /// <summary>The ActivitySource name to subscribe to or register with a tracing SDK.</summary>
    public const string ActivitySourceName = "Configlue";

    /// <summary>The Meter name to subscribe to or register with a metrics SDK.</summary>
    public const string MeterName = "Configlue";

    internal static readonly ActivitySource Activities = new(ActivitySourceName);
    private static readonly Meter Metrics = new(MeterName);
    private static readonly Histogram<double> ResolveDuration = Metrics.CreateHistogram<double>(
        "configlue.resolve.duration",
        "s"
    );
    private static readonly Histogram<double> ReadDuration = Metrics.CreateHistogram<double>(
        "configlue.source.read.duration",
        "s"
    );
    private static readonly Histogram<double> WriteDuration = Metrics.CreateHistogram<double>(
        "configlue.source.write.duration",
        "s"
    );
    private static readonly Histogram<double> ReloadDuration = Metrics.CreateHistogram<double>(
        "configlue.reload.duration",
        "s"
    );
    private static readonly Histogram<double> MigrationDuration = Metrics.CreateHistogram<double>(
        "configlue.migration.duration",
        "s"
    );
    private static readonly Counter<long> ReloadFailures = Metrics.CreateCounter<long>(
        "configlue.reload.failures"
    );
    private static readonly Counter<long> WriteConflicts = Metrics.CreateCounter<long>(
        "configlue.source.write.conflicts"
    );
    private static readonly Counter<long> Migrations = Metrics.CreateCounter<long>(
        "configlue.migrations"
    );
    private static readonly Counter<long> MigrationFailures = Metrics.CreateCounter<long>(
        "configlue.migration.failures"
    );

    internal static bool HasObservers() =>
        Activities.HasListeners()
        || ResolveDuration.Enabled
        || ReadDuration.Enabled
        || WriteDuration.Enabled
        || ReloadDuration.Enabled
        || MigrationDuration.Enabled
        || ReloadFailures.Enabled
        || WriteConflicts.Enabled
        || Migrations.Enabled
        || MigrationFailures.Enabled;

    internal static bool IsEnabled(ConfiglueDiagnosticEventKind kind) =>
        Activities.HasListeners()
        || kind switch
        {
            ConfiglueDiagnosticEventKind.ResolveCompleted
            or ConfiglueDiagnosticEventKind.ResolveFailed => ResolveDuration.Enabled,
            ConfiglueDiagnosticEventKind.SourceReadCompleted
            or ConfiglueDiagnosticEventKind.SourceReadFailed => ReadDuration.Enabled,
            ConfiglueDiagnosticEventKind.WriteCompleted
            or ConfiglueDiagnosticEventKind.WriteFailed
            or ConfiglueDiagnosticEventKind.WriteConflict => WriteDuration.Enabled
                || WriteConflicts.Enabled,
            ConfiglueDiagnosticEventKind.ReloadCompleted
            or ConfiglueDiagnosticEventKind.ReloadFailed => ReloadDuration.Enabled
                || ReloadFailures.Enabled,
            ConfiglueDiagnosticEventKind.MigrationCompleted
            or ConfiglueDiagnosticEventKind.MigrationFailed => MigrationDuration.Enabled
                || Migrations.Enabled
                || MigrationFailures.Enabled,
            _ => false,
        };

    internal static string ActivityName(ConfiglueDiagnosticOperation operation) =>
        operation switch
        {
            ConfiglueDiagnosticOperation.Resolve => "configlue.resolve",
            ConfiglueDiagnosticOperation.SourceRead => "configlue.source.read",
            ConfiglueDiagnosticOperation.Write => "configlue.source.write",
            ConfiglueDiagnosticOperation.Reload => "configlue.reload",
            ConfiglueDiagnosticOperation.Migrate => "configlue.migrate",
            _ => "configlue.operation",
        };

    internal static void Record(in ConfiglueDiagnosticEvent diagnosticEvent)
    {
        var histogram = diagnosticEvent.Kind switch
        {
            ConfiglueDiagnosticEventKind.ResolveCompleted
            or ConfiglueDiagnosticEventKind.ResolveFailed => ResolveDuration,
            ConfiglueDiagnosticEventKind.SourceReadCompleted
            or ConfiglueDiagnosticEventKind.SourceReadFailed => ReadDuration,
            ConfiglueDiagnosticEventKind.WriteCompleted
            or ConfiglueDiagnosticEventKind.WriteFailed
            or ConfiglueDiagnosticEventKind.WriteConflict => WriteDuration,
            ConfiglueDiagnosticEventKind.ReloadCompleted
            or ConfiglueDiagnosticEventKind.ReloadFailed => ReloadDuration,
            ConfiglueDiagnosticEventKind.MigrationCompleted
            or ConfiglueDiagnosticEventKind.MigrationFailed => MigrationDuration,
            _ => null,
        };
        var counter = diagnosticEvent.Kind switch
        {
            ConfiglueDiagnosticEventKind.ReloadFailed when !diagnosticEvent.Canceled =>
                ReloadFailures,
            ConfiglueDiagnosticEventKind.WriteConflict => WriteConflicts,
            ConfiglueDiagnosticEventKind.MigrationCompleted => Migrations,
            ConfiglueDiagnosticEventKind.MigrationFailed when !diagnosticEvent.Canceled =>
                MigrationFailures,
            _ => null,
        };
        if (!(histogram?.Enabled ?? false) && !(counter?.Enabled ?? false))
            return;
        // Only fixed outcome values and configured reader types become metric labels.
        // State names, source IDs, subject keys, locators, revisions, and errors are excluded.
        var tags = new TagList { { "configlue.result", Result(diagnosticEvent) } };
        if (diagnosticEvent.SourceKind is not null)
            tags.Add("configlue.source.kind", diagnosticEvent.SourceKind);
        if (histogram?.Enabled ?? false)
            histogram.Record(diagnosticEvent.Duration.TotalSeconds, tags);
        if (counter?.Enabled ?? false)
            counter.Add(1, tags);
    }

    internal static string Result(in ConfiglueDiagnosticEvent diagnosticEvent)
    {
        if (diagnosticEvent.Canceled)
            return "canceled";
        if (diagnosticEvent.ReadStatus is { } status)
            return status switch
            {
                StateReadStatus.Success => "success",
                StateReadStatus.NotFound => "not_found",
                StateReadStatus.Unavailable => "unavailable",
                StateReadStatus.InvalidPayload => "invalid_payload",
                _ => "unknown",
            };
        return diagnosticEvent.Kind switch
        {
            ConfiglueDiagnosticEventKind.WriteConflict => "conflict",
            ConfiglueDiagnosticEventKind.ResolveFailed
            or ConfiglueDiagnosticEventKind.SourceReadFailed
            or ConfiglueDiagnosticEventKind.WriteFailed
            or ConfiglueDiagnosticEventKind.ReloadFailed
            or ConfiglueDiagnosticEventKind.MigrationFailed
            or ConfiglueDiagnosticEventKind.ValidationFailed
            or ConfiglueDiagnosticEventKind.ObserverFailed => "failed",
            _ => "success",
        };
    }
}

/// <summary>The runtime operation tracked for tracing/metrics. Internal only.</summary>
/// <remarks>
/// Public diagnostics expose only terminal outcomes (<see cref="ConfiglueDiagnosticEventKind"/>);
/// operation starts never leave this assembly and are observed through
/// <c>System.Diagnostics.ActivitySource</c>.
/// </remarks>
internal enum ConfiglueDiagnosticOperation
{
    Resolve,
    SourceRead,
    Write,
    Reload,
    Migrate,
}
