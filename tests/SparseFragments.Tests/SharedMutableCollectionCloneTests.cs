using SparseFragments;

namespace SparseFragments.Tests;

/// <summary>
/// Representative first-class collection matrix (issue #280): arrays, lists,
/// sets and dictionaries with alias/cycle preservation. Exotic containers
/// (queues, stacks, concurrent collections, priority queues, linked lists,
/// sorted/observable wrappers, immutable collections) are intentionally
/// unsupported and require a custom clone policy.
/// </summary>
[SparseFragmentModel]
public partial class SharedMutableCollectionRoot
{
    public int Value { get; set; }
    public int[] IntArray { get; set; } = [];
    public int[] IntArrayAlias { get; set; } = [];
    public IReadOnlyList<SharedMutableCollectionRoot> SequenceView { get; set; } =
        Array.Empty<SharedMutableCollectionRoot>();
    public List<SharedMutableCollectionRoot> List { get; set; } = new();
    public List<SharedMutableCollectionRoot> ListAlias { get; set; } = new();
    public IList<SharedMutableCollectionRoot> ListView { get; set; } =
        new List<SharedMutableCollectionRoot>();
    public HashSet<SharedMutableCollectionRoot> Set { get; set; } = new();
    public HashSet<SharedMutableCollectionRoot> SetAlias { get; set; } = new();
    public ISet<SharedMutableCollectionRoot> SetView { get; set; } =
        new HashSet<SharedMutableCollectionRoot>();
    public Dictionary<string, SharedMutableCollectionRoot> Dictionary { get; set; } = new();
    public Dictionary<string, SharedMutableCollectionRoot> DictionaryAlias { get; set; } = new();
    public IDictionary<string, SharedMutableCollectionRoot> DictionaryView { get; set; } =
        new Dictionary<string, SharedMutableCollectionRoot>();
}

public sealed class SharedMutableCollectionCloneTests
{
    [Test]
    public void ClonePreservesAliasesAndCyclesForFirstClassCollections()
    {
        var root = new SharedMutableCollectionRoot { Value = 1 };
        var other = new SharedMutableCollectionRoot { Value = 2 };
        var array = new[] { 1, 2 };
        var list = new List<SharedMutableCollectionRoot>([root, other]);
        var set = new HashSet<SharedMutableCollectionRoot>([root, other]);
        var dictionary = new Dictionary<string, SharedMutableCollectionRoot>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            ["Key"] = root,
            ["Other"] = other,
        };
        root.IntArray = root.IntArrayAlias = array;
        root.SequenceView = list;
        root.List = root.ListAlias = list;
        root.ListView = list;
        root.Set = root.SetAlias = set;
        root.SetView = set;
        root.Dictionary = root.DictionaryAlias = dictionary;
        root.DictionaryView = dictionary;

        var clone = root.DeepClone();
        var cloneOther = clone.List[1];

        ReferenceEquals(clone.IntArray, clone.IntArrayAlias).ShouldBeTrue();
        ReferenceEquals(clone.IntArray, array).ShouldBeFalse();
        clone.IntArray.ShouldBe([1, 2]);
        ReferenceEquals(clone.SequenceView, clone.List).ShouldBeTrue();
        ReferenceEquals(clone.List, clone.ListAlias).ShouldBeTrue();
        ReferenceEquals(clone.ListView, clone.List).ShouldBeTrue();
        ReferenceEquals(clone.List, list).ShouldBeFalse();
        ReferenceEquals(clone.List[0], clone).ShouldBeTrue();
        cloneOther.Value.ShouldBe(2);
        ReferenceEquals(clone.Set, clone.SetAlias).ShouldBeTrue();
        ReferenceEquals(clone.SetView, clone.Set).ShouldBeTrue();
        ReferenceEquals(clone.Set, set).ShouldBeFalse();
        clone.Set.Count.ShouldBe(2);
        ReferenceEquals(clone.Dictionary, clone.DictionaryAlias).ShouldBeTrue();
        ReferenceEquals(clone.DictionaryView, clone.Dictionary).ShouldBeTrue();
        ReferenceEquals(clone.Dictionary, dictionary).ShouldBeFalse();
        ReferenceEquals(clone.Dictionary["KEY"], clone).ShouldBeTrue();
        ReferenceEquals(clone.Dictionary["other"], cloneOther).ShouldBeTrue();

        clone.List[0] = cloneOther;
        list[0].ShouldBe(root);
    }
}
