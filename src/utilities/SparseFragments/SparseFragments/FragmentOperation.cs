namespace SparseFragments;

/// <summary>The requested mutation for one source-local fragment member.</summary>
public enum FragmentOperationKind
{
    /// <summary>Leave the source member unchanged.</summary>
    Unchanged,

    /// <summary>Set the source member, including setting it to null/default.</summary>
    Set,

    /// <summary>Remove the member from this source contribution.</summary>
    Unset,
}

/// <summary>A source-local mutation that distinguishes leaving a member alone from removing it.</summary>
public readonly struct FragmentOperation<T>
{
    private readonly T? _value;

    private FragmentOperation(FragmentOperationKind kind, T? value)
    {
        Kind = kind;
        _value = value;
    }

    /// <summary>The operation to apply.</summary>
    public FragmentOperationKind Kind { get; }

    /// <summary>The value for a set operation.</summary>
    public T? Value =>
        Kind == FragmentOperationKind.Set
            ? _value
            : throw new InvalidOperationException("This fragment operation does not set a value.");

    /// <summary>Leaves the source member unchanged.</summary>
    public static FragmentOperation<T> Unchanged => default;

    /// <summary>Sets the source member to a present value.</summary>
    public static FragmentOperation<T> Set(T? value) => new(FragmentOperationKind.Set, value);

    /// <summary>Creates a set operation from a value, so a patch member can be assigned directly.</summary>
    /// <param name="value">The value to set, including an explicit null or default.</param>
    public static implicit operator FragmentOperation<T>(T? value) => Set(value);

    /// <summary>Removes the source member contribution.</summary>
    public static FragmentOperation<T> Unset => new(FragmentOperationKind.Unset, default);

    /// <summary>Applies this operation to a current source member.</summary>
    public Optional<T> Apply(Optional<T> current) =>
        Kind switch
        {
            FragmentOperationKind.Unchanged => current,
            FragmentOperationKind.Set => Optional<T>.Present(_value),
            FragmentOperationKind.Unset => Optional<T>.Missing,
            _ => throw new InvalidOperationException($"Unknown fragment operation '{Kind}'."),
        };
}
