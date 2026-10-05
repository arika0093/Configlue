using BenchmarkDotNet.Attributes;
using Configlue;

[ConfiglueModel("bench-mutable-clone-child")]
public partial class MutableCloneChild
{
    public int Value { get; set; }
}

[ConfiglueModel("bench-mutable-clone-collections")]
public partial class MutableCloneCollections
{
    public List<MutableCloneChild> Items { get; set; } = [];
    public MutableCloneChild[] Array { get; set; } = [];
    public Dictionary<string, MutableCloneChild> Lookup { get; set; } = new();
    public HashSet<MutableCloneChild> Set { get; set; } = [];
}

/// <summary>Guards the recursive path while scalar collection copies are optimized.</summary>
[MemoryDiagnoser]
public class MutableCollectionCloneBenchmarks
{
    [Params(16, 256)]
    public int Count { get; set; }

    private MutableCloneCollections _model = null!;
    private MutableCloneCollections.Fragment _fragment = null!;

    [GlobalSetup]
    public void Setup()
    {
        var items = Enumerable
            .Range(0, Count)
            .Select(static value => new MutableCloneChild { Value = value })
            .ToList();
        _model = new()
        {
            Items = items,
            Array = items.ToArray(),
            Lookup = items.ToDictionary(static item => $"key-{item.Value}"),
            Set = items.ToHashSet(),
        };
        _fragment = MutableCloneCollections.Fragment.From(_model);
        var clone = _model.DeepClone();
        if (
            ReferenceEquals(clone.Items[0], items[0])
            || !ReferenceEquals(clone.Items[0], clone.Array[0])
            || !ReferenceEquals(clone.Items[0], clone.Lookup["key-0"])
            || !clone.Set.Contains(clone.Items[0])
        )
        {
            throw new InvalidOperationException("Mutable clone fixture lost isolation or aliases.");
        }
    }

    [Benchmark]
    public object Model() => _model.DeepClone();

    [Benchmark]
    public object Fragment() => _fragment.DeepClone();
}
