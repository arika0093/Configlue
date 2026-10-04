using System.Collections;
using Microsoft.Extensions.Logging;

namespace Configlue;

internal static class RuntimeDiagnosticLogging
{
    internal static void Log(ILogger? logger, in ConfiglueDiagnosticEvent diagnosticEvent)
    {
        if (logger is null)
            return;
        var level = diagnosticEvent.Canceled
            ? LogLevel.Debug
            : diagnosticEvent.Kind switch
            {
                ConfiglueDiagnosticEventKind.WriteConflict
                or ConfiglueDiagnosticEventKind.ValidationFailed => LogLevel.Warning,
                ConfiglueDiagnosticEventKind.ResolveFailed
                or ConfiglueDiagnosticEventKind.SourceReadFailed
                or ConfiglueDiagnosticEventKind.WriteFailed
                or ConfiglueDiagnosticEventKind.ReloadFailed
                or ConfiglueDiagnosticEventKind.MigrationFailed
                or ConfiglueDiagnosticEventKind.ObserverFailed => LogLevel.Error,
                ConfiglueDiagnosticEventKind.SourceFallback
                    when diagnosticEvent.ReadStatus == StateReadStatus.Unavailable =>
                    LogLevel.Warning,
                _ => LogLevel.Debug,
            };
        if (!logger.IsEnabled(level))
            return;
        logger.Log(
            level,
            new EventId(2000 + (int)diagnosticEvent.Kind, diagnosticEvent.Kind.ToString()),
            new LogState(diagnosticEvent),
            exception: null,
            static (state, _) => state.ToString()
        );
    }

    internal static bool IsEnabled(ILogger? logger, ConfiglueDiagnosticEventKind kind)
    {
        if (logger is null)
            return false;
        var level = kind switch
        {
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
            return logger.IsEnabled(LogLevel.Debug) || logger.IsEnabled(LogLevel.Warning);
        return logger.IsEnabled(level);
    }

    private readonly struct LogState(ConfiglueDiagnosticEvent diagnosticEvent)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public int Count => 9;
        public KeyValuePair<string, object?> this[int index] =>
            index switch
            {
                0 => new("EventKind", diagnosticEvent.Kind.ToString()),
                1 => new("StateName", diagnosticEvent.StateName),
                2 => new("SubjectKey", diagnosticEvent.SubjectKey.Value),
                3 => new("ModelId", diagnosticEvent.ModelId),
                4 => new("SourceId", diagnosticEvent.SourceId),
                5 => new("SourceKind", diagnosticEvent.SourceKind),
                6 => new("Result", ConfiglueTelemetry.Result(diagnosticEvent)),
                7 => new("DurationMilliseconds", diagnosticEvent.Duration.TotalMilliseconds),
                8 => new("ErrorCategory", diagnosticEvent.ErrorCategory),
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
                yield return this[index];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() =>
            $"{diagnosticEvent.Kind}: model {diagnosticEvent.ModelId}, state {diagnosticEvent.StateName}, subject {diagnosticEvent.SubjectKey}, source {diagnosticEvent.SourceId}, result {ConfiglueTelemetry.Result(diagnosticEvent)}, duration {diagnosticEvent.Duration.TotalMilliseconds:F3} ms.";
    }
}
