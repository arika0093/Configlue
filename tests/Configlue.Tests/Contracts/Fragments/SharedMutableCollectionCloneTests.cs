using Configlue;

namespace Configlue.Tests;

/// <summary>
/// Configlue-generator parity smoke for the narrowed first-class collection matrix
/// (issue #280). The representative alias/cycle matrix lives once in
/// <c>SparseFragments.Tests.SharedMutableCollectionCloneTests</c>; this contract
/// only proves the Configlue generator honors the same shapes.
/// </summary>
[ConfiglueModel("shared-mutable-collection-clone")]
public partial class SharedMutableCollectionRoot
{
    public int Value { get; set; }
    public List<SharedMutableCollectionRoot> List { get; set; } = new();
    public List<SharedMutableCollectionRoot> ListAlias { get; set; } = new();
    public HashSet<SharedMutableCollectionRoot> Set { get; set; } = new();
    public HashSet<SharedMutableCollectionRoot> SetAlias { get; set; } = new();
    public Dictionary<string, SharedMutableCollectionRoot> Dictionary { get; set; } = new();
    public Dictionary<string, SharedMutableCollectionRoot> DictionaryAlias { get; set; } = new();
}

public sealed class SharedMutableCollectionCloneTests
{
    [Test]
    public void ClonePreservesAliasesForFirstClassCollections()
    {
        var root = new SharedMutableCollectionRoot { Value = 1 };
        var other = new SharedMutableCollectionRoot { Value = 2 };
        var list = new List<SharedMutableCollectionRoot>([root, other]);
        var set = new HashSet<SharedMutableCollectionRoot>([root, other]);
        var dictionary = new Dictionary<string, SharedMutableCollectionRoot>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            ["Key"] = root,
            ["Other"] = other,
        };
        root.List = root.ListAlias = list;
        root.Set = root.SetAlias = set;
        root.Dictionary = root.DictionaryAlias = dictionary;

        var clone = root.DeepClone();
        var cloneOther = clone.List[1];

        ReferenceEquals(clone.List, clone.ListAlias).ShouldBeTrue();
        ReferenceEquals(clone.List, list).ShouldBeFalse();
        ReferenceEquals(clone.List[0], clone).ShouldBeTrue();
        cloneOther.Value.ShouldBe(2);
        ReferenceEquals(clone.Set, clone.SetAlias).ShouldBeTrue();
        ReferenceEquals(clone.Set, set).ShouldBeFalse();
        clone.Set.Count.ShouldBe(2);
        ReferenceEquals(clone.Dictionary, clone.DictionaryAlias).ShouldBeTrue();
        ReferenceEquals(clone.Dictionary, dictionary).ShouldBeFalse();
        ReferenceEquals(clone.Dictionary["KEY"], clone).ShouldBeTrue();
        ReferenceEquals(clone.Dictionary["other"], cloneOther).ShouldBeTrue();
    }
}
