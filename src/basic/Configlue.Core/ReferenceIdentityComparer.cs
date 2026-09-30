using System.Runtime.CompilerServices;

namespace Configlue;

internal sealed class ReferenceIdentityComparer : IEqualityComparer<object>
{
    public static ReferenceIdentityComparer Instance { get; } = new();

    public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);

    public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
}
