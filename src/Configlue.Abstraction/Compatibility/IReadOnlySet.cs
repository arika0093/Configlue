#if NETSTANDARD
#pragma warning disable CS1591
namespace System.Collections.Generic
{
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public interface IReadOnlySet<T> : IReadOnlyCollection<T>
    {
        bool Contains(T item);

        bool IsProperSubsetOf(IEnumerable<T> other);

        bool IsProperSupersetOf(IEnumerable<T> other);

        bool IsSubsetOf(IEnumerable<T> other);

        bool IsSupersetOf(IEnumerable<T> other);

        bool Overlaps(IEnumerable<T> other);

        bool SetEquals(IEnumerable<T> other);
    }
}
#pragma warning restore CS1591
#endif
