using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Configlue.CompilerServices;

/// <summary>
/// Reference-identity comparer used by generated deep-clone helpers.
/// </summary>
/// <remarks>
/// <c>System.Collections.Generic.ReferenceEqualityComparer</c> only exists on .NET 5+, and the
/// polyfilled variant is internal, so generated code targets this portable public type instead.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class ConfiglueReferenceEqualityComparer : IEqualityComparer<object>
{
    /// <summary>Gets the shared comparer instance.</summary>
    public static ConfiglueReferenceEqualityComparer Instance { get; } = new();

    private ConfiglueReferenceEqualityComparer() { }

    /// <inheritdoc />
    public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);

    /// <inheritdoc />
    public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
}
