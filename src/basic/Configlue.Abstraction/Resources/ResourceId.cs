namespace Configlue.Resources;

/// <summary>Identifies a physical resource independently of the logical sources that expose it.</summary>
/// <remarks>
/// A <see cref="ResourceId"/> describes the physical coordination and atomicity domain used to group
/// writes. It does not imply that every batch writer reporting the same identity is interchangeable;
/// callers that combine writers must additionally satisfy <see cref="IResourceBatchCompatibility"/>.
/// </remarks>
public readonly record struct ResourceId
{
    /// <summary>Creates a resource identity.</summary>
    public ResourceId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    /// <summary>The stable, provider-defined identity value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
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
