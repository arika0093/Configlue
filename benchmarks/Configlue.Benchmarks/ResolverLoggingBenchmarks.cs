using BenchmarkDotNet.Attributes;
using Configlue.Resources;
using Configlue.Sources;
using Configlue.State;
using Configlue.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

[MemoryDiagnoser]
public class ResolverLoggingBenchmarks
{
    private StateSourceResolver<BenchmarkSettings.Fragment> _resolver = null!;

    [Params(1, 4, 16)]
    public int SourceCount { get; set; }

    [Params("None", "Disabled", "Enabled")]
    public string Logging { get; set; } = "None";

    [GlobalSetup]
    public void Setup()
    {
        var sources = Enumerable
            .Range(0, SourceCount)
            .Select(index => new StateSource<BenchmarkSettings.Fragment>(
                $"source-{index}",
                index == SourceCount - 1
                    ? new InMemoryStateSource<BenchmarkSettings.Fragment>(new() { Counter = 10 })
                    : new InMemoryStateSource<BenchmarkSettings.Fragment>(),
                new StateSourceOptions<BenchmarkSettings.Fragment>
                {
                    Priority = SourceCount - index,
                    FallbackCondition = StateFallbackCondition.NotFound,
                }
            ))
            .ToArray();
        ILogger? logger = Logging switch
        {
            "Disabled" => NullLogger.Instance,
            "Enabled" => new CountingLogger(),
            _ => null,
        };
        _resolver = new StateSourceResolver<BenchmarkSettings.Fragment>(new(sources), logger);
        var result = _resolver.ReadAsync(ConfiglueResourceContext.Default).GetAwaiter().GetResult();
        if (result.Status != StateReadStatus.Success || result.Value?.Counter.Value != 10)
        {
            throw new InvalidOperationException("Resolver did not return the expected value.");
        }
    }

    [Benchmark]
    public ValueTask<StateReadResult<BenchmarkSettings.Fragment>> ReadAsync() =>
        _resolver.ReadAsync(ConfiglueResourceContext.Default);

    private sealed class CountingLogger : ILogger
    {
        private long _count;

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => _count++;
    }
}
