using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.Resources;
using Configlue.Sources;
using Configlue.State;
using Configlue.Testing;

/// <summary>
/// Issue #219: measures retained watch-topology allocations separately from #168 scratch pooling.
///
/// The resolver rents temporary revision buffers per read (pooled scratch, returned before the
/// read completes) but retains two independently owned values per resolution: the
/// <see cref="StateRevisionVector"/> and the watcher topology/state. These benchmarks isolate
/// the retained topology cost:
/// <list type="bullet">
/// <item>temporary scratch: pooled <c>ArrayPool</c> buffers shared by both read benchmarks, so
/// they cancel out of the cold-minus-stable delta;</item>
/// <item><c>StateRevisionVector</c> ownership: measured alone by
/// <see cref="ResolverStableRepeatBenchmarks219.ConstructRevisionVector"/>;</item>
/// <item>retained watcher topology/state: the <c>ColdTopologyReadAsync</c> (fresh resolver,
/// topology rebuilt) minus <c>StableRepeatReadAsync</c> (same resolver, routing unchanged,
/// immutable topology reused) delta. The single-source stable case needs no heap array for
/// its one watch target.</item>
/// </list>
/// </summary>
[ConfiglueModel("bench-resolver-retained-219", Version = 1)]
public partial class ResolverRetained219Settings
{
    public int Counter { get; set; }
}

/// <summary>
/// Steady-state resolver reads with stable watch topology plus isolated revision-vector
/// ownership. The resolver is built once in <c>GlobalSetup</c> and warmed, so measured
/// iterations exercise the topology-reuse path.
/// </summary>
[MemoryDiagnoser]
public class ResolverStableRepeatBenchmarks219
{
    private StateSourceResolver<ResolverRetained219Settings.Fragment> _resolver = null!;
    private StateRevision[] _vectorScratch = [];

    [Params(1, 2, 4, 16)]
    public int SourceCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _resolver = new StateSourceResolver<ResolverRetained219Settings.Fragment>(
            ResolverRetained219Sources.CreateSourceSet(SourceCount)
        );
        // Warm the immutable topology so measured iterations are stable-topology repeats.
        _ = _resolver.ReadAsync(ConfiglueResourceContext.Default).GetAwaiter().GetResult();

        _vectorScratch = new StateRevision[SourceCount];
        for (var index = 0; index < _vectorScratch.Length; index++)
        {
            _vectorScratch[index] = new StateRevision(
                SourceId.From($"bench-219-{index}"),
                $"revision-{index}"
            );
        }

        _ = StateRevisionVector.FromSpan(_vectorScratch);
    }

    /// <summary>
    /// Repeated reads with unchanged source/context routing. Retained topology is reused;
    /// per-read retained state is the <c>Resolution</c>, the revision vector, and the compact
    /// observed-revision state (a single inline revision for one source, an exact-size array
    /// otherwise). Temporary scratch buffers are pooled and cancel out of comparisons.
    /// </summary>
    [Benchmark]
    public ValueTask<
        StateReadResult<ResolverRetained219Settings.Fragment>
    > StableRepeatReadAsync() => _resolver.ReadAsync(ConfiglueResourceContext.Default);

    /// <summary>
    /// Isolates <c>StateRevisionVector</c> ownership for the same source count so the
    /// retained-topology delta (cold minus stable read) can be read separately from vector costs.
    /// </summary>
    [Benchmark]
    public StateRevisionVector ConstructRevisionVector() =>
        StateRevisionVector.FromSpan(_vectorScratch);
}

/// <summary>
/// Cold resolver reads where each measured iteration builds retained watch topology on a fresh
/// resolver over a shared source set. Compare against
/// <see cref="ResolverStableRepeatBenchmarks219.StableRepeatReadAsync"/> for the same
/// <c>SourceCount</c>; the delta is the retained topology/state rebuild avoided on stable repeats.
/// </summary>
[MemoryDiagnoser]
public class ResolverColdTopologyBenchmarks219
{
    private StateSourceSet<ResolverRetained219Settings.Fragment> _sourceSet = null!;
    private StateSourceResolver<ResolverRetained219Settings.Fragment> _resolver = null!;

    [Params(1, 2, 4, 16)]
    public int SourceCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _sourceSet = ResolverRetained219Sources.CreateSourceSet(SourceCount);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Fresh resolver (not measured) so the benchmarked read always rebuilds retained topology.
        _resolver = new StateSourceResolver<ResolverRetained219Settings.Fragment>(_sourceSet);
    }

    [Benchmark]
    public ValueTask<
        StateReadResult<ResolverRetained219Settings.Fragment>
    > ColdTopologyReadAsync() => _resolver.ReadAsync(ConfiglueResourceContext.Default);
}

internal static class ResolverRetained219Sources
{
    public static StateSourceSet<ResolverRetained219Settings.Fragment> CreateSourceSet(
        int sourceCount
    )
    {
        var sources = Enumerable
            .Range(0, sourceCount)
            .Select(index =>
            {
                // The deepest source holds the value, so the retained topology spans all
                // inspected sources for the given count (1, 2, 4, or 16 routes).
                var store =
                    index == sourceCount - 1
                        ? new InMemoryStateSource<ResolverRetained219Settings.Fragment>(
                            new ResolverRetained219Settings.Fragment
                            {
                                Counter = Optional<int>.Present(index),
                            }
                        )
                        : new InMemoryStateSource<ResolverRetained219Settings.Fragment>();
                return new StateSource<ResolverRetained219Settings.Fragment>(
                    $"bench-219-{index}",
                    store,
                    new StateSourceOptions<ResolverRetained219Settings.Fragment>
                    {
                        Priority = sourceCount - index,
                        FallbackCondition = StateFallbackCondition.NotFound,
                    }
                );
            })
            .ToArray();
        return new StateSourceSet<ResolverRetained219Settings.Fragment>(sources);
    }
}
