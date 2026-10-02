using System.Collections;
using System.Collections.Generic;
using SparseFragments;

namespace PackageSparse.NetStandard.Consumer;

[SparseFragmentModel]
public partial class SetSettings
{
    public SetSettings() { }

    public ISet<string> Mutable { get; set; } = new PortableSet<string>(new HashSet<string>());

    public IReadOnlySet<string> ReadOnly { get; set; } =
        new PortableSet<string>(new HashSet<string>());
}

public static class SetConsumer
{
    public static SetSettings Clone(SetSettings settings) => settings.DeepClone();
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
