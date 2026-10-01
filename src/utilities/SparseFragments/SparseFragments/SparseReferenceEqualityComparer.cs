using System.Runtime.CompilerServices;

namespace SparseFragments;

/// <summary>Reference equality comparer used by generated clone helpers.</summary>
public sealed class SparseReferenceEqualityComparer : IEqualityComparer<object>
{
    /// <summary>The shared comparer instance.</summary>
    public static SparseReferenceEqualityComparer Instance { get; } = new();

    private SparseReferenceEqualityComparer() { }

    /// <summary>Compares two references for identity.</summary>
    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

    /// <summary>Returns the identity hash code.</summary>
    public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
}
