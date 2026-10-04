using BenchmarkDotNet.Attributes;
using Configlue.Resources;
using Configlue.State;

[MemoryDiagnoser]
public class ReadResultFactoryBenchmarks
{
    [Benchmark]
    public StateReadResult<int> StateSuccess() => StateReadResult<int>.Success(42, "revision");

    [Benchmark]
    public StateReadResult<int> StateNotFound() => StateReadResult<int>.NotFound("revision");

    [Benchmark]
    public ResourceReadResult ResourceSuccess() =>
        ResourceReadResult.Success(ReadOnlyMemory<byte>.Empty, "revision");

    [Benchmark]
    public ResourceReadResult ResourceNotFound() => ResourceReadResult.NotFound("revision");
}
