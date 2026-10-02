using Configlue;

namespace Configlue.Tests;

[ConfiglueModel("shared-set-clone")]
public partial class SharedSetSettings
{
    public ISet<string> Mutable { get; set; } = new HashSet<string>();
    public IReadOnlySet<string> ReadOnly { get; set; } = new HashSet<string>();
}

public sealed class SharedSetCloneTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void ClonePreservesSetAliasesAndComparersAcrossInterfaces(bool sorted)
    {
        ISet<string> set = sorted
            ? new SortedSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add("Z");
        set.Add("a");
        var model = new SharedSetSettings { Mutable = set, ReadOnly = (IReadOnlySet<string>)set };
        var clone = model.DeepClone();
        ReferenceEquals(clone.Mutable, clone.ReadOnly).ShouldBeTrue();
        ReferenceEquals(clone.Mutable, set).ShouldBeFalse();
        clone.Mutable.GetType().ShouldBe(set.GetType());
        clone.Mutable.Contains("A").ShouldBeTrue();
        if (sorted)
            clone.Mutable.First().ShouldBe("a");
        clone.Mutable.Remove("A");
        set.Contains("a").ShouldBeTrue();
    }
}
