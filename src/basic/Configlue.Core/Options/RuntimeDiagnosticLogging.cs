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
                ConfiglueDiagnosticEventKind.ResolveStarted
                or ConfiglueDiagnosticEventKind.SourceReadStarted
                or ConfiglueDiagnosticEventKind.WriteStarted
                or ConfiglueDiagnosticEventKind.ReloadStarted
                or ConfiglueDiagnosticEventKind.MigrationStarted
                or ConfiglueDiagnosticEventKind.WatchStarted
                or ConfiglueDiagnosticEventKind.WatchStopped => LogLevel.Trace,
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

    private readonly struct LogState(ConfiglueDiagnosticEvent diagnosticEvent)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public int Count => 11;
        public KeyValuePair<string, object?> this[int index] =>
            index switch
            {
                0 => new("EventKind", diagnosticEvent.Kind.ToString()),
                1 => new("StateName", diagnosticEvent.StateName),
                2 => new("ModelId", diagnosticEvent.ModelId),
                3 => new("OperationId", diagnosticEvent.OperationId),
                4 => new("ParentOperationId", diagnosticEvent.ParentOperationId),
                5 => new("SourceId", diagnosticEvent.SourceId),
                6 => new("SourceKind", diagnosticEvent.SourceKind),
                7 => new("Result", ConfiglueTelemetry.Result(diagnosticEvent)),
                8 => new("DurationMilliseconds", diagnosticEvent.Duration.TotalMilliseconds),
                9 => new("ErrorCategory", diagnosticEvent.ErrorCategory),
                10 => new("TraceId", diagnosticEvent.TraceId),
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
                yield return this[index];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() =>
            $"{diagnosticEvent.Kind}: model {diagnosticEvent.ModelId}, state {diagnosticEvent.StateName}, source {diagnosticEvent.SourceId}, result {ConfiglueTelemetry.Result(diagnosticEvent)}, duration {diagnosticEvent.Duration.TotalMilliseconds:F3} ms.";
    }
}
