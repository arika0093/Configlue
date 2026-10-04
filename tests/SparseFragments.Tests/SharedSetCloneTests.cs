using System.Collections;
using SparseFragments;

namespace SparseFragments.Tests;

[SparseFragmentModel]
public partial class SharedSetSettings
{
    public ISet<string> Mutable { get; set; } = new PortableSet<string>(new HashSet<string>());
    public IReadOnlySet<string> ReadOnly { get; set; } =
        new PortableSet<string>(new HashSet<string>());
}

internal sealed class PortableSet<T> : ISet<T>, IReadOnlySet<T>
{
    private readonly ISet<T> _inner;

    public PortableSet(ISet<T> inner) => _inner = inner;

    public int Count => _inner.Count;

    public bool IsReadOnly => _inner.IsReadOnly;

    public bool Add(T item) => _inner.Add(item);

    void ICollection<T>.Add(T item) => _inner.Add(item);

    public void Clear() => _inner.Clear();

    public bool Contains(T item) => _inner.Contains(item);

    public void CopyTo(T[] array, int arrayIndex) => _inner.CopyTo(array, arrayIndex);

    public bool Remove(T item) => _inner.Remove(item);

    public IEnumerator<T> GetEnumerator() => _inner.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _inner.GetEnumerator();

    public void ExceptWith(IEnumerable<T> other) => _inner.ExceptWith(other);

    public void IntersectWith(IEnumerable<T> other) => _inner.IntersectWith(other);

    public bool IsProperSubsetOf(IEnumerable<T> other) => _inner.IsProperSubsetOf(other);

    public bool IsProperSupersetOf(IEnumerable<T> other) => _inner.IsProperSupersetOf(other);

    public bool IsSubsetOf(IEnumerable<T> other) => _inner.IsSubsetOf(other);

    public bool IsSupersetOf(IEnumerable<T> other) => _inner.IsSupersetOf(other);

    public bool Overlaps(IEnumerable<T> other) => _inner.Overlaps(other);

    public bool SetEquals(IEnumerable<T> other) => _inner.SetEquals(other);

    public void SymmetricExceptWith(IEnumerable<T> other) => _inner.SymmetricExceptWith(other);

    public void UnionWith(IEnumerable<T> other) => _inner.UnionWith(other);
}

[SparseFragmentModel]
public partial class ReadOnlyFirstSetSettings
{
    public IReadOnlySet<string> ReadOnly { get; set; } = new PortableHashSet<string>();
    public ISet<string> Mutable { get; set; } = new HashSet<string>();
}

internal sealed class PortableHashSet<T> : HashSet<T>, IReadOnlySet<T>
{
    public PortableHashSet() { }

    public PortableHashSet(IEqualityComparer<T> comparer)
        : base(comparer) { }
}

public sealed class SharedSetCloneTests
{
    // Representative set matrix (issue #280): HashSet comparer semantics and
    // mutable/read-only alias preservation in both member orders. SortedSet and
    // other specialized containers are intentionally unsupported.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void ReadOnlyAliasesPreserveComparerInBothMemberOrders(bool readOnlyFirst)
    {
        ISet<string> shared = new PortableHashSet<string>(StringComparer.OrdinalIgnoreCase);
        shared.Add("Z");
        shared.Add("a");
        ISet<string> mutable;
        IReadOnlySet<string> readOnly;
        if (readOnlyFirst)
        {
            var clone = new ReadOnlyFirstSetSettings
            {
                Mutable = shared,
                ReadOnly = (IReadOnlySet<string>)shared,
            }
                .DeepClone()
                .DeepClone();
            mutable = clone.Mutable;
            readOnly = clone.ReadOnly;
        }
        else
        {
            var clone = new SharedSetSettings
            {
                Mutable = shared,
                ReadOnly = (IReadOnlySet<string>)shared,
            }
                .DeepClone()
                .DeepClone();
            mutable = clone.Mutable;
            readOnly = clone.ReadOnly;
        }
        ReferenceEquals(mutable, readOnly).ShouldBeTrue();
        readOnly.Contains("A").ShouldBeTrue();
        readOnly.SetEquals(new[] { "z", "A" }).ShouldBeTrue();
        mutable.Remove("A").ShouldBeTrue();
        readOnly.Contains("a").ShouldBeFalse();
        shared.Contains("a").ShouldBeTrue();
    }

    [Test]
    public void ClonePreservesSetComparerForConcreteSources()
    {
        ISet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add("Z");
        set.Add("a");
        var model = new SharedSetSettings
        {
            Mutable = set,
            ReadOnly = new PortableSet<string>(new HashSet<string>()),
        };
        var clone = model.DeepClone();
        clone.Mutable.Contains("A").ShouldBeTrue();
        clone.Mutable.Remove("A");
        set.Contains("a").ShouldBeTrue();
    }

    [Test]
    public void CloneCopiesPlainReadOnlySetContents()
    {
        var model = new SharedSetSettings
        {
            ReadOnly = new PortableSet<string>(new HashSet<string> { "alpha", "beta" }),
        };
        var clone = model.DeepClone();
        clone.ReadOnly.Count.ShouldBe(2);
        clone.ReadOnly.Contains("alpha").ShouldBeTrue();
        clone.ReadOnly.Contains("beta").ShouldBeTrue();
    }

    [Test]
    public void RecloningPreservesReadOnlySetComparer()
    {
        ISet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add("Z");
        set.Add("a");
        var model = new SharedSetSettings
        {
            Mutable = set,
            ReadOnly = new PortableSet<string>(new HashSet<string>()),
        };
        var reclone = model.DeepClone().DeepClone();
        reclone.Mutable.Contains("A").ShouldBeTrue();
    }

    [Test]
    public void ClonePreservesAliasesBetweenMutableAndReadOnlyViews()
    {
        var shared = new PortableSet<string>(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Z", "a" }
        );
        var model = new SharedSetSettings { Mutable = shared, ReadOnly = shared };
        var clone = model.DeepClone();
        ReferenceEquals(clone.Mutable, clone.ReadOnly).ShouldBeTrue();
        ReferenceEquals(clone.Mutable, shared).ShouldBeFalse();
        clone.ReadOnly.Contains("a").ShouldBeTrue();
        clone.Mutable.Remove("a");
        shared.Contains("a").ShouldBeTrue();
    }
}
