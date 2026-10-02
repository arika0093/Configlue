namespace Configlue.Resources;

/// <summary>Identifies a physical resource independently of the logical sources that expose it.</summary>
/// <remarks>
/// A <see cref="ResourceId"/> describes the physical coordination and atomicity domain used to group
/// writes. It does not imply that every batch writer reporting the same identity is interchangeable;
/// callers that combine writers must additionally satisfy <see cref="IResourceBatchCompatibility"/>.
/// The default value has no identity and is only valid as the output of an unsuccessful
/// <see cref="ITryResourceIdentity.TryGetResourceId(ConfiglueResourceContext, out ResourceId)"/> call.
/// </remarks>
public readonly record struct ResourceId
{
    private readonly string? _value;

    /// <summary>Creates a resource identity.</summary>
    public ResourceId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    /// <summary>The stable, provider-defined identity value, or an empty string for the default value.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Gets whether this value is the uninitialized default and carries no resource identity.</summary>
    public bool IsDefault => _value is null;

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Exposes the physical identity shared by resources and their logical views.</summary>
public interface IResourceIdentity
{
    /// <summary>Gets the identity used for one resource operation.</summary>
    ResourceId GetResourceId(ConfiglueResourceContext context);
}

/// <summary>Optionally forwards a physical identity when one is available for an operation context.</summary>
public interface ITryResourceIdentity
{
    /// <summary>Tries to get the physical identity used for an operation context.</summary>
    bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId resourceId);
}
