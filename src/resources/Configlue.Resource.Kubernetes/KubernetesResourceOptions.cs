namespace Configlue.Resource.Kubernetes;

/// <summary>Options for a resource backed by one Kubernetes ConfigMap or Secret key.</summary>
/// <remarks>
/// A null key selects whole-object mode: the complete key set is mapped
/// deterministically (sorted keys, binary entries as base64 JSON). A non-null key selects
/// single-key mode. ConfigMap data entries are UTF-8 text; ConfigMap binaryData and Secret
/// entries are raw bytes.
/// </remarks>
public sealed class KubernetesResourceOptions
{
    /// <summary>
    /// An advanced fixed identity override shared by every operation context. Configuring this
    /// asserts that all selected namespaces, names, and keys share one physical coordination domain;
    /// an incorrect value can make batch grouping unsafe.
    /// </summary>
    public ResourceId? FixedResourceId { get; init; }

    /// <summary>Resolves the namespace for each subject-aware operation.</summary>
    /// <remarks>
    /// Selectors should return stable values for a given context. Resolving a different
    /// namespace addresses a different physical object and yields a different resource identity.
    /// </remarks>
    public Func<ConfiglueResourceContext, string>? NamespaceSelector { get; init; }

    /// <summary>Resolves the object name for each subject-aware operation.</summary>
    public Func<ConfiglueResourceContext, string>? NameSelector { get; init; }

    /// <summary>Resolves the key for each subject-aware operation.</summary>
    public Func<ConfiglueResourceContext, string>? KeySelector { get; init; }

    /// <summary>The initial watch reconnect delay after a stream terminates without an event.</summary>
    public TimeSpan WatchReconnectInitialDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>The maximum watch reconnect delay.</summary>
    public TimeSpan WatchReconnectMaxDelay { get; init; } = TimeSpan.FromSeconds(5);

#pragma warning disable S3928 // Validation reports the invalid options property.
    internal void Validate(bool keyRequired)
    {
        if (WatchReconnectInitialDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(WatchReconnectInitialDelay));
        }

        if (WatchReconnectMaxDelay < WatchReconnectInitialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(WatchReconnectMaxDelay));
        }
    }
#pragma warning restore S3928
}
