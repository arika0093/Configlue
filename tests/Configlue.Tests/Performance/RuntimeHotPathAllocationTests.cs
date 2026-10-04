#if NET10_0_OR_GREATER
using Configlue.State;
using Microsoft.Extensions.Logging;

namespace Configlue.Tests;

[ConfiglueModel("runtime-no-validation-budget")]
public partial class NoValidationBudgetSettings
{
    public string Name { get; set; } = "default";
    public int Counter { get; set; }
}

public sealed class RuntimeHotPathAllocationTests
{
    [Test]
    [Arguments(1)]
    [Arguments(4)]
    [Arguments(16)]
    public void StableResolverRead_DoesNotAllocateRevisionOrWatchSnapshots(int sourceCount)
    {
        var sources = new StateSourceSet<int>(
            Enumerable
                .Range(0, sourceCount)
                .Select(index => new StateSource<int>(
                    $"source-{index}",
                    new CachedReader(
                        index == sourceCount - 1
                            ? StateReadResult<int>.Success(42, "r")
                            : StateReadResult<int>.NotFound()
                    ),
                    new StateSourceOptions<int>
                    {
                        Priority = sourceCount - index,
                        FallbackCondition = StateFallbackCondition.NotFound,
                    }
                ))
        );
        var resolver = new StateSourceResolver<int>(sources);
        var result = default(StateReadResult<int>);
        var allocated = Measure(() =>
            result = resolver.ReadAsync(ConfiglueResourceContext.Default).GetAwaiter().GetResult()
        );
        result.Value.ShouldBe(42);
        allocated.ShouldBe(0);
    }

    [Test]
    [Arguments(1)]
    [Arguments(16)]
    public void ResolverLogging_DoesNotAllocateBeyondUnloggedRead(int sourceCount)
    {
        var sources = new StateSourceSet<int>(
            Enumerable
                .Range(0, sourceCount)
                .Select(index => new StateSource<int>(
                    $"source-{index}",
                    new CachedReader(
                        index == sourceCount - 1
                            ? StateReadResult<int>.Success(42, "r")
                            : StateReadResult<int>.NotFound()
                    ),
                    new StateSourceOptions<int>
                    {
                        Priority = sourceCount - index,
                        FallbackCondition = StateFallbackCondition.NotFound,
                    }
                ))
        );
        var withoutLog = new StateSourceResolver<int>(sources);
        var disabled = new StateSourceResolver<int>(sources, new CountingLogger(false));
        var enabled = new StateSourceResolver<int>(sources, new CountingLogger(true));
        var result = default(StateReadResult<int>);
        var baselineBytes = Measure(() =>
            result = withoutLog.ReadAsync(ConfiglueResourceContext.Default).GetAwaiter().GetResult()
        );
        var disabledBytes = Measure(() =>
            result = disabled.ReadAsync(ConfiglueResourceContext.Default).GetAwaiter().GetResult()
        );
        var enabledBytes = Measure(() =>
            result = enabled.ReadAsync(ConfiglueResourceContext.Default).GetAwaiter().GetResult()
        );
        result.Value.ShouldBe(42);
        disabledBytes.ShouldBe(baselineBytes);
        enabledBytes.ShouldBe(baselineBytes);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void ValidationWithoutAttributes_WarmedMetadataAndPruningAllocateNothing(
        bool dataAnnotations
    )
    {
        var recorder = new RuntimeDiagnosticRecorder(
            "budget",
            "runtime-no-validation-budget",
            1,
            ConfiglueRuntimeDiagnosticOptions.Disabled,
            []
        );
        var pipeline = new RuntimeValidationPipeline<
            NoValidationBudgetSettings,
            NoValidationBudgetSettings.Fragment
        >([], dataAnnotations, "budget", recorder);
        var model = new NoValidationBudgetSettings { Name = "benchmark", Counter = 10 };
        var fragment = RuntimeModel<
            NoValidationBudgetSettings,
            NoValidationBudgetSettings.Fragment
        >.ToFragment(model);
        var defaults = RuntimeModel<
            NoValidationBudgetSettings,
            NoValidationBudgetSettings.Fragment
        >.ToFragment(new());
        var source = new StateSource<NoValidationBudgetSettings.Fragment>(
            "budget",
            new FragmentReader(fragment),
            new()
        );
        IConfiglueFragment? pruned = null;
        var allocated = Measure(() =>
        {
            pipeline.Validate(model);
            pipeline.ValidateResolvedModel(model, fragment);
            pruned = pipeline.PruneInvalidMembers(source, fragment, defaults);
            if (!dataAnnotations)
            {
                pipeline.ValidateContribution(source, fragment, defaults);
            }
        });
        ReferenceEquals(pruned, fragment).ShouldBeTrue();
        allocated.ShouldBe(0);
    }

    private static long Measure(Action action)
    {
        for (var index = 0; index < 100; index++)
            action();
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
            action();
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private sealed class CachedReader(StateReadResult<int> result) : ISourceReader<int>
    {
        public ValueTask<StateReadResult<int>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => new(result);
    }

    private sealed class FragmentReader(NoValidationBudgetSettings.Fragment fragment)
        : ISourceReader<NoValidationBudgetSettings.Fragment>
    {
        private readonly StateReadResult<NoValidationBudgetSettings.Fragment> _result =
            StateReadResult<NoValidationBudgetSettings.Fragment>.Success(fragment);

        public ValueTask<StateReadResult<NoValidationBudgetSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => new(_result);
    }

    private sealed class CountingLogger(bool enabled) : ILogger
    {
        private long _count;

        public bool IsEnabled(LogLevel logLevel) => enabled;

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
#endif
