// Focused SparseFragments benchmarks for issue #318.
//
// Covers the representative operations named in the issue on a small but
// realistic model (scalars, nested model, appended collection):
// sparse construction, Fragment.From, shallow merge, nested merge,
// diff, patch apply, and deep clone.
//
// Baselines are intentionally modest: straightforward hand-written merge and
// deep-clone implementations over plain models, plus a minimal
// reflection-based deep clone. They exist to establish realistic cost and
// allocation profiles, not to manufacture a favorable comparison.
//
// All groups use MemoryDiagnoser so results include throughput/time (Mean)
// and allocations (Allocated plus Gen0/Gen1/Gen2) where applicable.
// This group is for local measurement only; it is not a CI performance gate.
//
// Run locally in Release mode:
//
//   dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*SparseFragmentBenchmarks318*'
//
// Fast smoke check (numbers are not publishable):
//
//   dotnet run -c Release --project benchmarks/Configlue.Benchmarks -- --filter '*SparseFragmentBenchmarks318*' --job dry
//
// Compare within the same machine, runtime, power mode, and configuration.
// Do not transcribe BenchmarkDotNet numbers into unit-test thresholds.
using BenchmarkDotNet.Attributes;
using SparseFragments;

[SparseFragmentModel]
public partial class SparseBenchChild318
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5432;
}

[SparseFragmentModel]
public partial class SparseBenchSettings318
{
    public bool Enabled { get; set; } = true;

    public int RetryCount { get; set; } = 3;

    public string? Label { get; set; } = "default";

    public SparseBenchChild318? Nested { get; set; } = new();

    [SparseMerge(MergeMode.Append)]
    public IReadOnlyList<string> Plugins { get; set; } = [];
}

/// <summary>
/// Plain-model overlay used by the hand-written merge baseline.
/// Nullable reference / value members mean "unspecified, fall through";
/// non-null members override the lower layer.
/// </summary>
public sealed class SparseBenchHandOverlay318
{
    public bool? Enabled { get; set; }

    public int? RetryCount { get; set; }

    public string? Label { get; set; }

    public bool HasLabel { get; set; }

    public SparseBenchChild318? Nested { get; set; }

    public bool HasNested { get; set; }

    public List<string>? Plugins { get; set; }
}

[MemoryDiagnoser]
public class SparseFragmentBenchmarks318
{
    private SparseBenchSettings318 _model = null!;
    private SparseBenchSettings318 _before = null!;
    private SparseBenchSettings318 _after = null!;
    private SparseBenchSettings318.Fragment _lower = null!;
    private SparseBenchSettings318.Fragment _shallowHigher = null!;
    private SparseBenchSettings318.Fragment _nestedHigher = null!;
    private SparseBenchSettings318.Fragment _diff = null!;
    private SparseBenchSettings318.Fragment _applyTarget = null!;
    private SparseBenchSettings318.Patch _patch = null!;
    private SparseBenchHandOverlay318 _handHigher = null!;
    private SparseBenchSettings318 _handLower = null!;

    private static readonly System.Reflection.PropertyInfo[] ReflectionCloneProperties =
        typeof(SparseBenchSettings318).GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
        );

    [GlobalSetup]
    public void Setup()
    {
        _model = new SparseBenchSettings318
        {
            Enabled = true,
            RetryCount = 7,
            Label = "bench",
            Nested = new SparseBenchChild318 { Host = "db.local", Port = 6432 },
            Plugins = ["base-plugin"],
        };
        _before = new SparseBenchSettings318
        {
            Enabled = true,
            RetryCount = 3,
            Label = "before",
            Nested = new SparseBenchChild318 { Host = "db.local", Port = 5432 },
            Plugins = ["base-plugin"],
        };
        _after = new SparseBenchSettings318
        {
            Enabled = false,
            RetryCount = 3,
            Label = "after",
            Nested = new SparseBenchChild318 { Host = "db.local", Port = 6432 },
            Plugins = ["base-plugin", "extra-plugin"],
        };

        _lower = SparseBenchSettings318.Fragment.From(_before);
        _shallowHigher = new SparseBenchSettings318.Fragment { Enabled = false, RetryCount = 9 };
        _nestedHigher = new SparseBenchSettings318.Fragment
        {
            Nested = new SparseBenchChild318.Fragment { Port = 6432 },
            Plugins = new[] { "extra-plugin" },
        };
        _diff = SparseBenchSettings318.Fragment.Diff(_before, _after);
        _applyTarget = SparseBenchSettings318.Fragment.From(_before);

        _patch = new SparseBenchSettings318.Patch { Label = "patched" };
        _patch.Nested.Port = 9000;

        _handLower = _before;
        _handHigher = new SparseBenchHandOverlay318
        {
            Enabled = false,
            Label = "patched",
            HasLabel = true,
            Nested = new SparseBenchChild318 { Host = "db.local", Port = 9000 },
            HasNested = true,
            Plugins = ["extra-plugin"],
        };
    }

    [Benchmark(Description = "Sparse construction: only set members become present")]
    public SparseBenchSettings318.Fragment SparseConstruction() =>
        new() { Label = "bench", RetryCount = 7 };

    [Benchmark(Description = "Fragment.From: full model becomes fully present")]
    public SparseBenchSettings318.Fragment FragmentFrom() =>
        SparseBenchSettings318.Fragment.From(_model);

    [Benchmark(Description = "Merge: shallow scalar overlay")]
    public SparseBenchSettings318.Fragment MergeShallow() => _lower.Merge(_shallowHigher);

    [Benchmark(Description = "Merge: nested model plus appended collection")]
    public SparseBenchSettings318.Fragment MergeNested() => _lower.Merge(_nestedHigher);

    [Benchmark(Description = "Hand-written baseline: plain-model layered merge")]
    public SparseBenchSettings318 HandWrittenMerge() => MergeHandWritten(_handLower, _handHigher);

    [Benchmark(Description = "Diff: minimal delta between two models")]
    public SparseBenchSettings318.Fragment Diff() =>
        SparseBenchSettings318.Fragment.Diff(_before, _after);

    [Benchmark(Description = "ApplyChanges: apply a diff onto a fragment")]
    public SparseBenchSettings318.Fragment ApplyChanges() => _applyTarget.ApplyChanges(_diff);

    [Benchmark(Description = "Apply: apply a typed patch onto a fragment")]
    public SparseBenchSettings318.Fragment ApplyPatch() => _applyTarget.Apply(_patch);

    [Benchmark(Description = "DeepClone: independent copy of a model graph")]
    public SparseBenchSettings318 ModelDeepClone() => _model.DeepClone();

    [Benchmark(Description = "DeepClone: independent copy of a fragment")]
    public SparseBenchSettings318.Fragment FragmentDeepClone() => _lower.DeepClone();

    [Benchmark(Description = "Reflection baseline: property-copy deep clone")]
    public SparseBenchSettings318 ReflectionClone() => CloneViaReflection(_model);

    private static SparseBenchSettings318 MergeHandWritten(
        SparseBenchSettings318 lower,
        SparseBenchHandOverlay318 higher
    )
    {
        // Straightforward hand-written layered merge with the same shape as the
        // generated Merge: unspecified higher-layer members fall through.
        var nested =
            higher is { HasNested: true, Nested: not null }
                ? new SparseBenchChild318 { Host = higher.Nested.Host, Port = higher.Nested.Port }
            : lower.Nested is null ? null
            : new SparseBenchChild318 { Host = lower.Nested.Host, Port = lower.Nested.Port };
        var plugins = higher.Plugins is null
            ? lower.Plugins
            : [.. lower.Plugins, .. higher.Plugins];
        return new SparseBenchSettings318
        {
            Enabled = higher.Enabled ?? lower.Enabled,
            RetryCount = higher.RetryCount ?? lower.RetryCount,
            Label = higher.HasLabel ? higher.Label : lower.Label,
            Nested = nested,
            Plugins = plugins,
        };
    }

    private static SparseBenchSettings318 CloneViaReflection(SparseBenchSettings318 value)
    {
        // Minimal reflection-based copy baseline: one Activator call plus a
        // per-property GetValue/SetValue round trip. Nested models and the
        // plugin list are copied so the clone is independent like DeepClone.
        var clone = (SparseBenchSettings318)(
            Activator.CreateInstance(typeof(SparseBenchSettings318))!
        );
        foreach (var property in ReflectionCloneProperties)
        {
            var propertyValue = property.GetValue(value);
            if (
                ReferenceEquals(propertyValue, value.Nested)
                && propertyValue is SparseBenchChild318 nested
            )
            {
                property.SetValue(
                    clone,
                    new SparseBenchChild318 { Host = nested.Host, Port = nested.Port }
                );
            }
            else if (
                ReferenceEquals(propertyValue, value.Plugins)
                && propertyValue is IReadOnlyList<string> plugins
            )
            {
                property.SetValue(clone, new List<string>(plugins));
            }
            else
            {
                property.SetValue(clone, propertyValue);
            }
        }

        return clone;
    }
}
