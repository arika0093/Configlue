using BenchmarkDotNet.Attributes;
using Configlue;

/// <summary>Measures generated deep clones without storage or resolver costs.</summary>
[MemoryDiagnoser]
public class CollectionCloneBenchmarks
{
    [Params(0, 16, 4096)]
    public int Count { get; set; }

    private FragmentEqualityCollectionSettings.Fragment _list = null!;
    private FragmentEqualityCollectionSettings.Fragment _set = null!;
    private FragmentEqualityCollectionSettings.Fragment _dictionary = null!;
    private FragmentEqualityCollectionSettings.Fragment _all = null!;

    [GlobalSetup]
    public void Setup()
    {
        var numbers = Enumerable.Range(0, Count).ToList();
        var lookup = numbers.ToDictionary(static value => $"key-{value}");
        var tags = lookup.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _list = new() { Numbers = Optional<List<int>>.Present(numbers) };
        _set = new() { Tags = Optional<HashSet<string>>.Present(tags) };
        _dictionary = new() { Lookup = Optional<Dictionary<string, int>>.Present(lookup) };
        _all = new()
        {
            Numbers = _list.Numbers,
            Scores = Optional<int[]>.Present(numbers.ToArray()),
            Lookup = _dictionary.Lookup,
            Tags = _set.Tags,
        };
        var copy = _all.DeepClone();
        if (!ConfiglueFragmentComparer.AreEqual(_all, copy))
        {
            throw new InvalidOperationException("Clone fixture changed values.");
        }
    }

    [Benchmark]
    public object List() => _list.DeepClone();

    [Benchmark]
    public object Set() => _set.DeepClone();

    [Benchmark]
    public object Dictionary() => _dictionary.DeepClone();

    [Benchmark]
    public object AllCollections() => _all.DeepClone();
}
