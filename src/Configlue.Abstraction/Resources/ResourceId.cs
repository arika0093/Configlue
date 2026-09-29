namespace Configlue.Resources;

/// <summary>Identifies a physical resource independently of the logical sources that expose it.</summary>
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
    /// <summary>The identity of the underlying physical resource.</summary>
    ResourceId ResourceId { get; }

    /// <summary>Gets the physical identity used for an operation on one subject.</summary>
    ResourceId GetResourceId(ConfiglueResourceContext context) => ResourceId;

    /// <summary>Tries to get the physical identity used for an operation on one subject.</summary>
    bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId resourceId)
    {
        resourceId = GetResourceId(context);
        return true;
    }
}
