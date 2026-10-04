using Configlue.State;
using Microsoft.Extensions.Logging;

namespace Configlue.Tests;

public sealed class ResolverLoggingTests
{
    [Test]
    public async Task ChangingThresholdAfterConstructionChangesEmittedLogs()
    {
        var logger = new RecordingLogger { MinimumLevel = LogLevel.None };
        var resolver = Create(logger, new FixedReader(StateReadResult<int>.Success(42, "r")));
        (await resolver.ReadAsync(ConfiglueResourceContext.Default)).Value.ShouldBe(42);
        logger.Entries.Count.ShouldBe(0);

        logger.MinimumLevel = LogLevel.Trace;
        await resolver.ReadAsync(ConfiglueResourceContext.Default);
        logger.Entries.Count.ShouldBe(2);
        logger.Entries[0].Level.ShouldBe(LogLevel.Trace);
        logger.Entries[0].EventId.ShouldBe(new EventId(1050, "ResolverSourceRead"));
        logger.Entries[0].Message.ShouldBe("Reading state source source-0.");
        logger.Entries[1].Level.ShouldBe(LogLevel.Debug);
        logger.Entries[1].Message.ShouldBe("State source source-0 returned Success.");
        logger.Entries[1].Values["SourceId"].ShouldBe(SourceId.From("source-0"));
        logger.Entries[1].Values["ReadStatus"].ShouldBe(StateReadStatus.Success);
        logger
            .Entries[1]
            .Values["{OriginalFormat}"]
            .ShouldBe("State source {SourceId} returned {ReadStatus}.");

        logger.MinimumLevel = LogLevel.None;
        await resolver.ReadAsync(ConfiglueResourceContext.Default);
        logger.Entries.Count.ShouldBe(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FallbackPreservesSeverityAndStructuredFields(bool unavailable)
    {
        var logger = new RecordingLogger();
        var resolver = Create(
            logger,
            new FixedReader(
                unavailable ? StateReadResult<int>.Unavailable() : StateReadResult<int>.NotFound()
            ),
            new FixedReader(StateReadResult<int>.Success(42))
        );
        (await resolver.ReadAsync(ConfiglueResourceContext.Default)).Value.ShouldBe(42);
        var fallback = logger.Entries.Single(entry => entry.EventId.Id == 1051);
        fallback.Level.ShouldBe(unavailable ? LogLevel.Warning : LogLevel.Debug);
        fallback.EventId.Name.ShouldBe("ResolverSourceFallback");
        fallback.Values["SourceId"].ShouldBe(SourceId.From("source-0"));
        fallback.Values["FallbackAction"].ShouldBe("continues");
        fallback
            .Values["ReadStatus"]
            .ShouldBe(unavailable ? StateReadStatus.Unavailable : StateReadStatus.NotFound);
    }

    [Test]
    public async Task SourceFailurePreservesExceptionAndErrorCategory()
    {
        var logger = new RecordingLogger();
        var expected = new InvalidOperationException("reader failed");
        var resolver = Create(logger, new ThrowingReader(expected));
        var actual = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await resolver.ReadAsync(ConfiglueResourceContext.Default)
        );
        ReferenceEquals(actual, expected).ShouldBeTrue();
        var failure = logger.Entries.Single(entry => entry.Level == LogLevel.Error);
        failure.EventId.Id.ShouldBe(1050);
        failure.Values["ErrorCategory"].ShouldBe(typeof(InvalidOperationException).FullName);
        failure.Exception.ShouldBeNull();
    }

    private static StateSourceResolver<int> Create(
        ILogger logger,
        params ISourceReader<int>[] readers
    ) =>
        new(
            new StateSourceSet<int>(
                readers.Select(
                    (reader, index) =>
                        new StateSource<int>(
                            $"source-{index}",
                            reader,
                            new StateSourceOptions<int>
                            {
                                Priority = readers.Length - index,
                                FallbackCondition =
                                    StateFallbackCondition.NotFound
                                    | StateFallbackCondition.Unavailable,
                            }
                        )
                )
            ),
            logger
        );

    private sealed class FixedReader(StateReadResult<int> result) : ISourceReader<int>
    {
        public ValueTask<StateReadResult<int>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => new(result);
    }

    private sealed class ThrowingReader(Exception exception) : ISourceReader<int>
    {
        public ValueTask<StateReadResult<int>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => throw exception;
    }

    private sealed record Entry
    {
        public LogLevel Level { get; init; }
        public EventId EventId { get; init; }
        public string Message { get; init; } = string.Empty;
        public Dictionary<string, object?> Values { get; init; } = new();
        public Exception? Exception { get; init; }
    }

    private sealed class RecordingLogger : ILogger
    {
        public LogLevel MinimumLevel { get; set; } = LogLevel.Trace;
        public List<Entry> Entries { get; } = [];

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= MinimumLevel && MinimumLevel != LogLevel.None;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (!IsEnabled(logLevel))
                return;
            var values = ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(
                pair => pair.Key,
                pair => pair.Value
            );
            Entries.Add(
                new Entry
                {
                    Level = logLevel,
                    EventId = eventId,
                    Message = formatter(state, exception),
                    Values = values,
                    Exception = exception,
                }
            );
        }
    }
}
