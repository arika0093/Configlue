namespace Configlue;

/// <summary>A value that distinguishes an absent member from a present null or default value.</summary>
public readonly struct Optional<T> : IEquatable<Optional<T>>
{
    private readonly T? _value;

    private Optional(T? value)
    {
        _value = value;
        IsPresent = true;
    }

    /// <summary>Whether the member was present in its source.</summary>
    public bool IsPresent { get; }

    /// <summary>The member value. Throws when the member is missing.</summary>
    public T? Value => IsPresent
        ? _value
        : throw new InvalidOperationException("A missing optional value has no value.");

    /// <summary>A missing member.</summary>
    public static Optional<T> Missing => default;

    /// <summary>A present member, including a present null or default value.</summary>
    public static Optional<T> Present(T? value) => new(value);

    /// <summary>Returns the value when present, or the supplied fallback when missing.</summary>
    public T? GetValueOrDefault(T? fallback = default) => IsPresent ? _value : fallback;

    /// <summary>Copies the presence and value of another member.</summary>
    public void CopyTo(ref Optional<T> destination) => destination = this;

    /// <summary>Whether this member is missing or present with a value.</summary>
    public override string ToString() => IsPresent ? $"Present({_value})" : "Missing";

    /// <inheritdoc />
    public bool Equals(Optional<T> other) =>
        IsPresent == other.IsPresent && (!IsPresent || EqualityComparer<T?>.Default.Equals(_value, other._value));

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Optional<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => IsPresent ? HashCode.Combine(true, _value) : 0;

    /// <summary>Creates a present member from a value.</summary>
    public static implicit operator Optional<T>(T? value) => Present(value);

    /// <summary>Compares optional values.</summary>
    public static bool operator ==(Optional<T> left, Optional<T> right) => left.Equals(right);

    /// <summary>Compares optional values.</summary>
    public static bool operator !=(Optional<T> left, Optional<T> right) => !left.Equals(right);
}
