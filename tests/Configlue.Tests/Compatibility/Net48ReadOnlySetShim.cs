#if NET48
namespace System.Collections.Generic;

// Test-only IReadOnlySet<T> shim for the net48 test target. The .NET Framework 4.8
// BCL has no System.Collections.Generic.IReadOnlySet<T>, and the public polyfill
// was removed from Configlue.Abstraction by design (#143), so test models that use
// read-only sets need this declaration to compile and generate on net48.
// Mirrors tests/fixtures/consumers/set-clones/CompatibilityIReadOnlySet.cs.
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
