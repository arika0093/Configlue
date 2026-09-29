namespace Configlue;

/// <summary>Identifies an opaque physical placement route for subject-specific resource operations.</summary>
public readonly record struct RouteKey
{
    private readonly string? _value;

    private RouteKey(string value) => _value = value;

    /// <summary>The default physical route.</summary>
    public static RouteKey Default => default;

    /// <summary>Whether this key selects the default physical route.</summary>
    public bool IsDefault => _value is null;

    /// <summary>The opaque provider-independent route token.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Creates a route token from an application-defined opaque value.</summary>
    public static RouteKey From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new RouteKey(value);
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
