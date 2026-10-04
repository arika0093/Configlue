namespace Configlue.Resource.Kubernetes;

/// <summary>A point-in-time view of a ConfigMap object.</summary>
public sealed class KubernetesConfigMapSnapshot
{
    /// <summary>Creates a ConfigMap snapshot.</summary>
    public KubernetesConfigMapSnapshot(
        string? resourceVersion,
        bool immutable,
        IReadOnlyDictionary<string, string> data,
        IReadOnlyDictionary<string, byte[]> binaryData
    )
    {
        ResourceVersion = resourceVersion;
        Immutable = immutable;
        Data = data ?? throw new ArgumentNullException(nameof(data));
        BinaryData = binaryData ?? throw new ArgumentNullException(nameof(binaryData));
    }

    /// <summary>The object resourceVersion used as revision metadata.</summary>
    public string? ResourceVersion { get; }

    /// <summary>Whether the object is immutable.</summary>
    public bool Immutable { get; }

    /// <summary>The UTF-8 string entries.</summary>
    public IReadOnlyDictionary<string, string> Data { get; }

    /// <summary>The binary entries.</summary>
    public IReadOnlyDictionary<string, byte[]> BinaryData { get; }
}

/// <summary>A point-in-time view of a Secret object.</summary>
public sealed class KubernetesSecretSnapshot
{
    /// <summary>Creates a Secret snapshot.</summary>
    public KubernetesSecretSnapshot(
        string? resourceVersion,
        bool immutable,
        IReadOnlyDictionary<string, byte[]> data
    )
    {
        ResourceVersion = resourceVersion;
        Immutable = immutable;
        Data = data ?? throw new ArgumentNullException(nameof(data));
    }

    /// <summary>The object resourceVersion used as revision metadata.</summary>
    public string? ResourceVersion { get; }

    /// <summary>Whether the object is immutable.</summary>
    public bool Immutable { get; }

    /// <summary>The decoded binary entries.</summary>
    public IReadOnlyDictionary<string, byte[]> Data { get; }
}

/// <summary>The kind of a Kubernetes watch notification.</summary>
public enum KubernetesWatchType
{
    /// <summary>An object was created or first observed.</summary>
    Added = 0,

    /// <summary>An object changed.</summary>
    Modified = 1,

    /// <summary>An object was deleted.</summary>
    Deleted = 2,

    /// <summary>A progress bookmark carrying a newer resourceVersion.</summary>
    Bookmark = 3,

    /// <summary>A terminal error frame (for example HTTP 410 Gone).</summary>
    Error = 4,
}

/// <summary>One decoded watch frame for a ConfigMap or Secret.</summary>
public sealed class KubernetesWatchEvent
{
    /// <summary>Creates a watch event.</summary>
    public KubernetesWatchEvent(
        KubernetesWatchType type,
        string? resourceVersion,
        KubernetesConfigMapSnapshot? configMap = null,
        KubernetesSecretSnapshot? secret = null,
        string? errorCode = null,
        string? errorMessage = null
    )
    {
        Type = type;
        ResourceVersion = resourceVersion;
        ConfigMap = configMap;
        Secret = secret;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    /// <summary>The notification kind.</summary>
    public KubernetesWatchType Type { get; }

    /// <summary>The bookmark or object resourceVersion when supplied.</summary>
    public string? ResourceVersion { get; }

    /// <summary>The ConfigMap payload for ConfigMap events.</summary>
    public KubernetesConfigMapSnapshot? ConfigMap { get; }

    /// <summary>The Secret payload for Secret events.</summary>
    public KubernetesSecretSnapshot? Secret { get; }

    /// <summary>The error code for error events (for example "Expired").</summary>
    public string? ErrorCode { get; }

    /// <summary>The error detail for error events. Never contains Secret values.</summary>
    public string? ErrorMessage { get; }

    /// <summary>Whether this event signals an expired resourceVersion.</summary>
    public bool IsExpired =>
        Type == KubernetesWatchType.Error
        && string.Equals(ErrorCode, "Expired", StringComparison.Ordinal);
}
