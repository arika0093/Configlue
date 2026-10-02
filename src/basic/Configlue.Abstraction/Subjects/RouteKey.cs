namespace Configlue;

/// <summary>Identifies an intermediate backend or placement route for resource operations.</summary>
/// <remarks>A route selects placement such as a region or shard; it does not identify a physical resource or its coordination domain.</remarks>
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
