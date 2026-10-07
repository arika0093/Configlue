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

    /// <summary>Creates a reference-identity clone context for generated deep-clone helpers.</summary>
    /// <remarks>Generated-code plumbing: referenced by emitted code, not hand-written callers.</remarks>
    public static Dictionary<object, object> CreateCloneContext() => new(Instance);

    /// <summary>Creates a reference-identity cycle scope for generated <c>Fragment.From</c> helpers.</summary>
    /// <remarks>Generated-code plumbing: referenced by emitted code, not hand-written callers.</remarks>
    public static HashSet<object> CreateFromCycleContext() => new(Instance);

    /// <summary>Creates a pair-identity cycle scope for generated <c>Fragment.Diff</c> helpers.</summary>
    /// <remarks>Generated-code plumbing: referenced by emitted code, not hand-written callers.</remarks>
    public static HashSet<KeyValuePair<object, object>> CreateDiffCycleContext() =>
        new(ConfiglueDiffPairEqualityComparer.Instance);

    /// <summary>Pair-identity comparer used by generated diff cycle scopes.</summary>
    /// <remarks>
    /// Two pairs are equal only when both keys and both values are reference-identical.
    /// </remarks>
    private sealed class ConfiglueDiffPairEqualityComparer
        : IEqualityComparer<KeyValuePair<object, object>>
    {
#pragma warning disable S3218 // Private nested comparer mirrors the outer singleton shape.
        public static ConfiglueDiffPairEqualityComparer Instance { get; } = new();
#pragma warning restore S3218

        private ConfiglueDiffPairEqualityComparer() { }

        public bool Equals(KeyValuePair<object, object> x, KeyValuePair<object, object> y) =>
            ReferenceEquals(x.Key, y.Key) && ReferenceEquals(x.Value, y.Value);

        public int GetHashCode(KeyValuePair<object, object> obj)
        {
            unchecked
            {
                var before = obj.Key is null ? 0 : RuntimeHelpers.GetHashCode(obj.Key);
                var after = obj.Value is null ? 0 : RuntimeHelpers.GetHashCode(obj.Value);
                return (before * 397) ^ after;
            }
        }
    }
}
