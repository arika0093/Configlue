using BenchmarkDotNet.Attributes;
using Configlue;

[ConfiglueModel("bench-fragequal-single", Version = 1)]
public partial class FragmentEqualitySingleSettings
{
    public string Name { get; set; } = "default";
}

[ConfiglueModel("bench-fragequal-four", Version = 1)]
public partial class FragmentEqualityFourSettings
{
    public string Name { get; set; } = "default";

    public int Counter { get; set; } = 3;

    public bool Enabled { get; set; } = true;

    public double Ratio { get; set; } = 1.5;
}

[ConfiglueModel("bench-fragequal-sixteen", Version = 1)]
public partial class FragmentEqualitySixteenSettings
{
    public string M01 { get; set; } = "v01";

    public string M02 { get; set; } = "v02";

    public string M03 { get; set; } = "v03";

    public string M04 { get; set; } = "v04";

    public int M05 { get; set; } = 5;

    public int M06 { get; set; } = 6;

    public int M07 { get; set; } = 7;

    public int M08 { get; set; } = 8;

    public bool M09 { get; set; } = true;

    public bool M10 { get; set; } = false;

    public bool M11 { get; set; } = true;

    public bool M12 { get; set; } = false;

    public double M13 { get; set; } = 1.3;

    public double M14 { get; set; } = 1.4;

    public string M15 { get; set; } = "v15";

    public string M16 { get; set; } = "v16";
}

[ConfiglueModel("bench-fragequal-child", Version = 1)]
public partial class FragmentEqualityChildSettings
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5432;
}

[ConfiglueModel("bench-fragequal-parent", Version = 1)]
public partial class FragmentEqualityParentSettings
{
    public string Name { get; set; } = "root";

    public FragmentEqualityChildSettings? Nested { get; set; } = new();
}

[ConfiglueModel("bench-fragequal-collections", Version = 1)]
public partial class FragmentEqualityCollectionSettings
{
    public List<int> Numbers { get; set; } = [];

    public int[] Scores { get; set; } = [];

    public Dictionary<string, int> Lookup { get; set; } = new();

    public HashSet<string> Tags { get; set; } = [];
}

/// <summary>
/// Measures sparse fragment equality over 1/4/16 present members, nested fragments, and
/// collection members. Both operands are distinct instances with equal content so the
/// reference-equality fast path never applies.
/// </summary>
[MemoryDiagnoser]
public class FragmentSparseEqualityBenchmarks
{
    private FragmentEqualitySingleSettings.Fragment _singleLeft = null!;
    private FragmentEqualitySingleSettings.Fragment _singleRight = null!;
    private FragmentEqualityFourSettings.Fragment _fourLeft = null!;
    private FragmentEqualityFourSettings.Fragment _fourRight = null!;
    private FragmentEqualitySixteenSettings.Fragment _sixteenLeft = null!;
    private FragmentEqualitySixteenSettings.Fragment _sixteenRight = null!;
    private FragmentEqualitySixteenSettings.Fragment _sixteenMismatched = null!;
    private FragmentEqualityParentSettings.Fragment _nestedLeft = null!;
    private FragmentEqualityParentSettings.Fragment _nestedRight = null!;
    private FragmentEqualityParentSettings.Fragment _nestedMismatched = null!;
    private FragmentEqualityCollectionSettings.Fragment _collectionsLeft = null!;
    private FragmentEqualityCollectionSettings.Fragment _collectionsRight = null!;

    [GlobalSetup]
    public void Setup()
    {
        _singleLeft = CreateSingle("default");
        _singleRight = CreateSingle("default");
        _fourLeft = CreateFour("default", 3);
        _fourRight = CreateFour("default", 3);
        _sixteenLeft = CreateSixteen("v16");
        _sixteenRight = CreateSixteen("v16");
        _sixteenMismatched = CreateSixteen("changed");
        _nestedLeft = CreateParent(5432);
        _nestedRight = CreateParent(5432);
        _nestedMismatched = CreateParent(6432);
        _collectionsLeft = CreateCollections();
        _collectionsRight = CreateCollections();
        if (
            !ConfiglueFragmentComparer.AreEqual(_singleLeft, _singleRight)
            || !ConfiglueFragmentComparer.AreEqual(_fourLeft, _fourRight)
            || !ConfiglueFragmentComparer.AreEqual(_sixteenLeft, _sixteenRight)
            || ConfiglueFragmentComparer.AreEqual(_sixteenLeft, _sixteenMismatched)
            || !ConfiglueFragmentComparer.AreEqual(_nestedLeft, _nestedRight)
            || ConfiglueFragmentComparer.AreEqual(_nestedLeft, _nestedMismatched)
            || !ConfiglueFragmentComparer.AreEqual(_collectionsLeft, _collectionsRight)
        )
        {
            throw new InvalidOperationException("Fragment equality fixtures did not compare as expected.");
        }
    }

    [Benchmark]
    public bool EqualSingle() => ConfiglueFragmentComparer.AreEqual(_singleLeft, _singleRight);

    [Benchmark]
    public bool EqualFour() => ConfiglueFragmentComparer.AreEqual(_fourLeft, _fourRight);

    [Benchmark]
    public bool EqualSixteen() => ConfiglueFragmentComparer.AreEqual(_sixteenLeft, _sixteenRight);

    [Benchmark]
    public bool UnequalSixteen() =>
        ConfiglueFragmentComparer.AreEqual(_sixteenLeft, _sixteenMismatched);

    [Benchmark]
    public bool EqualNested() => ConfiglueFragmentComparer.AreEqual(_nestedLeft, _nestedRight);

    [Benchmark]
    public bool UnequalNested() =>
        ConfiglueFragmentComparer.AreEqual(_nestedLeft, _nestedMismatched);

    [Benchmark]
    public bool EqualCollections() =>
        ConfiglueFragmentComparer.AreEqual(_collectionsLeft, _collectionsRight);

    private static FragmentEqualitySingleSettings.Fragment CreateSingle(string name) =>
        new() { Name = Optional<string>.Present(name) };

    private static FragmentEqualityFourSettings.Fragment CreateFour(string name, int counter) =>
        new()
        {
            Name = Optional<string>.Present(name),
            Counter = Optional<int>.Present(counter),
            Enabled = Optional<bool>.Present(true),
            Ratio = Optional<double>.Present(1.5),
        };

    private static FragmentEqualitySixteenSettings.Fragment CreateSixteen(string last) =>
        new()
        {
            M01 = Optional<string>.Present("v01"),
            M02 = Optional<string>.Present("v02"),
            M03 = Optional<string>.Present("v03"),
            M04 = Optional<string>.Present("v04"),
            M05 = Optional<int>.Present(5),
            M06 = Optional<int>.Present(6),
            M07 = Optional<int>.Present(7),
            M08 = Optional<int>.Present(8),
            M09 = Optional<bool>.Present(true),
            M10 = Optional<bool>.Present(false),
            M11 = Optional<bool>.Present(true),
            M12 = Optional<bool>.Present(false),
            M13 = Optional<double>.Present(1.3),
            M14 = Optional<double>.Present(1.4),
            M15 = Optional<string>.Present("v15"),
            M16 = Optional<string>.Present(last),
        };

    private static FragmentEqualityParentSettings.Fragment CreateParent(int port) =>
        new()
        {
            Name = Optional<string>.Present("root"),
            Nested = Optional<FragmentEqualityChildSettings.Fragment?>.Present(
                new FragmentEqualityChildSettings.Fragment
                {
                    Host = Optional<string>.Present("localhost"),
                    Port = Optional<int>.Present(port),
                }
            ),
        };

    private static FragmentEqualityCollectionSettings.Fragment CreateCollections()
    {
        return new()
        {
            Numbers = Optional<List<int>>.Present([1, 2, 3, 4, 5, 6, 7, 8]),
            Scores = Optional<int[]>.Present([8, 7, 6, 5, 4, 3, 2, 1]),
            Lookup = Optional<Dictionary<string, int>>.Present(
                new Dictionary<string, int>
                {
                    ["first"] = 1,
                    ["second"] = 2,
                    ["third"] = 3,
                    ["fourth"] = 4,
                }
            ),
            Tags = Optional<HashSet<string>>.Present(["alpha", "beta", "gamma", "delta"]),
        };
    }
}

/// <summary>
/// Measures value equality for lists, arrays, dictionaries, and sets with growing element
/// counts. The set cases expose the previous O(n^2) FindIndex/RemoveAt behavior: both the
/// equal and the last-element-mismatched comparisons must scale linearly.
/// </summary>
[MemoryDiagnoser]
public class FragmentValueEqualityBenchmarks
{
    [Params(16, 256, 4096)]
    public int Count { get; set; }

    private List<int> _leftList = null!;
    private List<int> _rightList = null!;
    private int[] _leftArray = null!;
    private int[] _rightArray = null!;
    private Dictionary<string, int> _leftDictionary = null!;
    private Dictionary<string, int> _rightDictionary = null!;
    private Dictionary<string, int> _mismatchedDictionary = null!;
    private HashSet<string> _leftSet = null!;
    private HashSet<string> _rightSet = null!;
    private HashSet<string> _mismatchedSet = null!;

    [GlobalSetup]
    public void Setup()
    {
        _leftList = Enumerable.Range(0, Count).ToList();
        _rightList = Enumerable.Range(0, Count).ToList();
        _leftArray = Enumerable.Range(0, Count).ToArray();
        _rightArray = Enumerable.Range(0, Count).ToArray();
        _leftDictionary = Enumerable.Range(0, Count).ToDictionary(static index => $"key-{index}");
        _rightDictionary = Enumerable
            .Range(0, Count)
            .ToDictionary(static index => $"key-{index}");
        _mismatchedDictionary = Enumerable
            .Range(0, Count)
            .ToDictionary(static index => $"key-{index}");
        _mismatchedDictionary[$"key-{Count - 1}"] = -1;
        _leftSet = Enumerable.Range(0, Count).Select(static index => $"item-{index}").ToHashSet();
        // Reverse insertion order proves set comparison ignores enumeration order.
        var count = Count;
        _rightSet = Enumerable
            .Range(0, count)
            .Select(index => $"item-{count - 1 - index}")
            .ToHashSet();
        _mismatchedSet = Enumerable
            .Range(0, Count)
            .Select(static index => $"item-{index}")
            .ToHashSet();
        _mismatchedSet.Remove($"item-{Count - 1}");
        _mismatchedSet.Add("item-missing");
        if (
            !ConfiglueValueComparer.AreEqual(_leftList, _rightList)
            || !ConfiglueValueComparer.AreEqual(_leftArray, _rightArray)
            || !ConfiglueValueComparer.AreEqual(_leftDictionary, _rightDictionary)
            || ConfiglueValueComparer.AreEqual(_leftDictionary, _mismatchedDictionary)
            || !ConfiglueValueComparer.AreEqual(_leftSet, _rightSet)
            || ConfiglueValueComparer.AreEqual(_leftSet, _mismatchedSet)
        )
        {
            throw new InvalidOperationException("Value equality fixtures did not compare as expected.");
        }
    }

    [Benchmark]
    public bool Lists() => ConfiglueValueComparer.AreEqual(_leftList, _rightList);

    [Benchmark]
    public bool Arrays() => ConfiglueValueComparer.AreEqual(_leftArray, _rightArray);

    [Benchmark]
    public bool Dictionaries() => ConfiglueValueComparer.AreEqual(_leftDictionary, _rightDictionary);

    [Benchmark]
    public bool DictionariesUnequal() =>
        ConfiglueValueComparer.AreEqual(_leftDictionary, _mismatchedDictionary);

    [Benchmark]
    public bool Sets() => ConfiglueValueComparer.AreEqual(_leftSet, _rightSet);

    [Benchmark]
    public bool SetsUnequal() => ConfiglueValueComparer.AreEqual(_leftSet, _mismatchedSet);
}
