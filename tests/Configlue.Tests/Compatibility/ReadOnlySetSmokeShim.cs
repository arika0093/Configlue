#if NET48 || NETSTANDARD2_0
namespace System.Collections.Generic;

// Test-only IReadOnlySet<T> shim for the net48/netstandard2.0 consumption smokes.
// Neither TFM ships System.Collections.Generic.IReadOnlySet<T>, and the public
// polyfill was removed from Configlue.Abstraction by design (#143), so smoke
// models that use read-only sets need this declaration to compile and generate.
// Mirrors the set-clones and package-sparse-netstandard consumer shims.
// Do NOT move this into src/: it must never ship in a package.
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
#endif
