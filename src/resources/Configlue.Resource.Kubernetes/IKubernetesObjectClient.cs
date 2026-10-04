namespace Configlue.Resource.Kubernetes;

/// <summary>
/// Shared Kubernetes object client used by ConfigMap and Secret resources.
/// Implementations remain caller-owned; resources never dispose them.
/// Secret values and cluster credentials must never appear in diagnostics.
/// </summary>
public interface IKubernetesObjectClient
{
    /// <summary>Reads one ConfigMap object.</summary>
    /// <exception cref="KubernetesObjectNotFoundException">The object does not exist.</exception>
    Task<KubernetesConfigMapSnapshot> GetConfigMapAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken
    );

    /// <summary>Reads one Secret object.</summary>
    /// <exception cref="KubernetesObjectNotFoundException">The object does not exist.</exception>
    Task<KubernetesSecretSnapshot> GetSecretAsync(
        string @namespace,
        string name,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Replaces one ConfigMap object with resourceVersion-aware optimistic concurrency.
    /// Unrelated keys are supplied by the caller; implementations persist exactly what they receive.
    /// </summary>
    /// <param name="namespace">The object namespace.</param>
    /// <param name="name">The object name.</param>
    /// <param name="data">The complete string entries.</param>
    /// <param name="binaryData">The complete binary entries.</param>
    /// <param name="expectedResourceVersion">The revision read earlier, or null for an unchecked write.</param>
    /// <param name="requireMissing">Requires the object to be absent.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The new resourceVersion.</returns>
    /// <exception cref="KubernetesConflictException">The object changed after it was read.</exception>
    /// <exception cref="KubernetesImmutableException">The object is immutable.</exception>
    Task<string?> ReplaceConfigMapAsync(
        string @namespace,
        string name,
        IReadOnlyDictionary<string, string> data,
        IReadOnlyDictionary<string, byte[]> binaryData,
        string? expectedResourceVersion,
        bool requireMissing,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Replaces one Secret object with resourceVersion-aware optimistic concurrency.
    /// Unrelated keys are supplied by the caller; implementations persist exactly what they receive.
    /// </summary>
    /// <param name="namespace">The object namespace.</param>
    /// <param name="name">The object name.</param>
    /// <param name="data">The complete decoded entries.</param>
    /// <param name="expectedResourceVersion">The revision read earlier, or null for an unchecked write.</param>
    /// <param name="requireMissing">Requires the object to be absent.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The new resourceVersion.</returns>
    /// <exception cref="KubernetesConflictException">The object changed after it was read.</exception>
    /// <exception cref="KubernetesImmutableException">The object is immutable.</exception>
    Task<string?> ReplaceSecretAsync(
        string @namespace,
        string name,
        IReadOnlyDictionary<string, byte[]> data,
        string? expectedResourceVersion,
        bool requireMissing,
        CancellationToken cancellationToken
    );

    /// <summary>Streams native watch frames for one ConfigMap object.</summary>
    /// <exception cref="KubernetesResourceExpiredException">The resourceVersion is stale (HTTP 410).</exception>
    IAsyncEnumerable<KubernetesWatchEvent> WatchConfigMapAsync(
        string @namespace,
        string name,
        string? resourceVersion,
        CancellationToken cancellationToken
    );

    /// <summary>Streams native watch frames for one Secret object.</summary>
    /// <exception cref="KubernetesResourceExpiredException">The resourceVersion is stale (HTTP 410).</exception>
    IAsyncEnumerable<KubernetesWatchEvent> WatchSecretAsync(
        string @namespace,
        string name,
        string? resourceVersion,
        CancellationToken cancellationToken
    );
}
