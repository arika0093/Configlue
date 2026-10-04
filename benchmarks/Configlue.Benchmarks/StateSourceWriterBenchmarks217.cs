using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.Resources;
using Configlue.Sources;
using Configlue.State;

/// <summary>
/// Repeated in-memory write microbenchmarks for the <see cref="StateSourceWriter{T}"/> routing
/// hot path (issue #217; complements the #211 generated write-routing and #214 mixed
/// transformer-pipeline coverage).
/// </summary>
/// <remarks>
/// Both benchmarks reuse one writer and one write request for repeated writes through the
/// production <see cref="StateSourceWriter{T}"/>: <see cref="ExplicitDefaultWriteAsync"/> uses
/// an explicit default over <see cref="SourceCount"/> writable sources (default last, so a
/// per-write scan would walk the whole set), and <see cref="InferredRootWriteAsync"/> uses a
/// single writable root among <see cref="SourceCount"/> sources (so a per-write inference would
/// rescan the set and repeat capability/ambiguity checks). Storage is a no-op in-memory writer
/// returning an already-completed <see cref="ValueTask{TResult}"/>, which isolates routing
/// overhead from storage I/O. Reintroducing per-write source-set scanning adds delegate/closure
/// and enumerator allocations per write and makes both cases scale with
/// <see cref="SourceCount"/>, regressing the <c>Allocated</c> column reported by
/// <see cref="MemoryDiagnoserAttribute"/>.
/// </remarks>
[MemoryDiagnoser]
public class StateSourceWriterBenchmarks217
{
    private StateSourceWriter<string> _explicitWriter = null!;
    private StateSourceWriter<string> _inferredWriter = null!;
    private StateWriteRequest<string> _request = null!;

    [Params(1, 16)]
    public int SourceCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _request = new StateWriteRequest<string>("benchmark");
        _explicitWriter = new StateSourceWriter<string>(
            CreateSourceSet(writableEvery: true),
            SourceId.From(SourceName(SourceCount - 1))
        );
        _inferredWriter = new StateSourceWriter<string>(
            CreateSourceSet(writableEvery: false)
        );

        // Warm the production write path (JIT) and fail fast on a misconfigured topology.
        _ = _explicitWriter
            .WriteAsync(ConfiglueResourceContext.Default, _request)
            .GetAwaiter()
            .GetResult();
        _ = _inferredWriter
            .WriteAsync(ConfiglueResourceContext.Default, _request)
            .GetAwaiter()
            .GetResult();
    }

    [Benchmark]
    public ValueTask<StateWriteResult> ExplicitDefaultWriteAsync() =>
        _explicitWriter.WriteAsync(ConfiglueResourceContext.Default, _request);

    [Benchmark]
    public ValueTask<StateWriteResult> InferredRootWriteAsync() =>
        _inferredWriter.WriteAsync(ConfiglueResourceContext.Default, _request);

    private StateSourceSet<string> CreateSourceSet(bool writableEvery)
    {
        var sources = new StateSource<string>[SourceCount];
        for (var index = 0; index < SourceCount; index++)
        {
            var store = new NoOpStateStore();
            var writable = writableEvery || index == SourceCount - 1;
            sources[index] = new StateSource<string>(
                SourceName(index),
                store,
                new StateSourceOptions<string> { Writer = writable ? store : null }
            );
        }

        return new StateSourceSet<string>(sources);
    }

    private static string SourceName(int index) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"source-{index:00}"
        );

    private sealed class NoOpStateStore : ISourceReader<string>, ISourceWriter<string>
    {
        public ValueTask<StateReadResult<string>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            _ = cancellationToken;
            return ValueTask.FromResult(StateReadResult<string>.NotFound());
        }

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<string> request,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            _ = request;
            _ = cancellationToken;
            return ValueTask.FromResult(new StateWriteResult("benchmark"));
        }
    }
}
