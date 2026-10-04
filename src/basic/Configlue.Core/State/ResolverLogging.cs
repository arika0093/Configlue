using Microsoft.Extensions.Logging;

namespace Configlue.State;

internal static class ResolverLogging
{
    private static readonly EventId ReadEvent = new(1050, "ResolverSourceRead");
    private static readonly EventId FallbackEvent = new(1051, "ResolverSourceFallback");

    internal static readonly Action<ILogger, SourceId, StateReadStatus, Exception?> Read =
        LoggerMessage.Define<SourceId, StateReadStatus>(
            LogLevel.Debug,
            ReadEvent,
            "State source {SourceId} returned {ReadStatus}."
        );

    internal static readonly Action<ILogger, SourceId, Exception?> ReadStarted =
        LoggerMessage.Define<SourceId>(
            LogLevel.Trace,
            ReadEvent,
            "Reading state source {SourceId}."
        );

    internal static readonly Action<ILogger, SourceId, string?, Exception?> ReadFailed =
        LoggerMessage.Define<SourceId, string?>(
            LogLevel.Error,
            ReadEvent,
            "Reading state source {SourceId} failed ({ErrorCategory})."
        );

    private static readonly Action<
        ILogger,
        SourceId,
        StateReadStatus,
        string,
        Exception?
    > DebugFallback = LoggerMessage.Define<SourceId, StateReadStatus, string>(
        LogLevel.Debug,
        FallbackEvent,
        "State source {SourceId} returned {ReadStatus}; fallback {FallbackAction}."
    );

    private static readonly Action<
        ILogger,
        SourceId,
        StateReadStatus,
        string,
        Exception?
    > WarningFallback = LoggerMessage.Define<SourceId, StateReadStatus, string>(
        LogLevel.Warning,
        FallbackEvent,
        "State source {SourceId} returned {ReadStatus}; fallback {FallbackAction}."
    );

    internal static void Fallback(
        ILogger logger,
        SourceId sourceId,
        StateReadStatus status,
        bool continues
    )
    {
        var log = status == StateReadStatus.Unavailable ? WarningFallback : DebugFallback;
        log(logger, sourceId, status, continues ? "continues" : "stops", null);
    }
}
