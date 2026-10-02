namespace Configlue;

/// <summary>Identifies one logical Configlue source registration in Core and runtime contracts.</summary>
/// <remarks>The default value is invalid; create IDs explicitly at string boundaries.</remarks>
public readonly record struct SourceId
{
    private readonly string? _value;

    /// <summary>Creates a logical source identifier.</summary>
    public SourceId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    /// <summary>The ordinal, stable source identifier, or an empty string for the default value.</summary>
    public string Value
    {
        get => _value ?? string.Empty;
        init => _value = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>Whether this value is uninitialized.</summary>
    public bool IsDefault => _value is null;

    /// <summary>Creates a source identifier at a string boundary.</summary>
    public static SourceId From(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;
}
