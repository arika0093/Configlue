using BenchmarkDotNet.Attributes;
using Configlue;

[MemoryDiagnoser]
public class RuntimeOperationLeaseBenchmarks
{
    private readonly RuntimeLifetime _lifetime = new();

    [Benchmark]
    public void EnterAndExit()
    {
        using var lease = _lifetime.EnterOperation();
    }

    [Benchmark]
    public async ValueTask EnterAndExitAsync()
    {
        using var lease = _lifetime.EnterOperation();
        await Task.Yield();
    }
}
