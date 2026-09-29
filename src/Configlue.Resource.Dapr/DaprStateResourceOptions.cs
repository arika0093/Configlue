using Dapr.Client;

namespace Configlue.Resource.Dapr;

/// <summary>Options for a resource backed by one Dapr State Management store key.</summary>
public sealed class DaprStateResourceOptions
{
    /// <summary>Resolves the store name for each subject-aware operation.</summary>
    /// <remarks>Selectors should return stable values for a given context.</remarks>
    public Func<ConfiglueResourceContext, string>? StoreNameSelector { get; init; }

    /// <summary>Resolves the state key for each subject-aware operation.</summary>
    public Func<ConfiglueResourceContext, string>? KeySelector { get; init; }

    /// <summary>Resolves an externally owned Dapr client for each subject-aware operation.</summary>
    /// <remarks>Use a distinct route when the selected client points to a different Dapr sidecar.</remarks>
    public Func<ConfiglueResourceContext, DaprClient>? ClientSelector { get; init; }

    /// <summary>The consistency mode used for state reads and writes.</summary>
    public ConsistencyMode? ConsistencyMode { get; init; }

    /// <summary>Provider-specific metadata sent with state operations.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>An optional stable identity overriding the identity derived from store and key.</summary>
    public ResourceId? ResourceId { get; init; }
}
