using Dapr.Client;

namespace Configlue.Resource.Dapr;

/// <summary>Options for a resource backed by one Dapr State Management store key.</summary>
public sealed class DaprStateResourceOptions
{
    /// <summary>The consistency mode used for state reads and writes.</summary>
    public ConsistencyMode? ConsistencyMode { get; init; }

    /// <summary>Provider-specific metadata sent with state operations.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>An optional stable identity overriding the identity derived from store and key.</summary>
    public ResourceId? ResourceId { get; init; }
}
