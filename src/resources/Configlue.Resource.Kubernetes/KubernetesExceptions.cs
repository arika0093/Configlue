using Configlue.State;

namespace Configlue.Resource.Kubernetes;

/// <summary>Raised when a Kubernetes object is missing.</summary>
public sealed class KubernetesObjectNotFoundException : Exception
{
    /// <summary>Creates a missing-object exception.</summary>
    public KubernetesObjectNotFoundException(string message)
        : base(message) { }
}

/// <summary>Raised when a Kubernetes write targets a stale resourceVersion.</summary>
public sealed class KubernetesConflictException : StateConflictException
{
    /// <summary>Creates a conflict exception.</summary>
    public KubernetesConflictException(string message)
        : base(message) { }
}

/// <summary>Raised when a write targets an immutable ConfigMap or Secret.</summary>
public sealed class KubernetesImmutableException : InvalidOperationException
{
    /// <summary>Creates an immutability exception.</summary>
    public KubernetesImmutableException(string message)
        : base(message) { }
}

/// <summary>Raised when a watch resourceVersion is expired (HTTP 410 Gone).</summary>
public sealed class KubernetesResourceExpiredException : Exception
{
    /// <summary>Creates an expired-version exception.</summary>
    public KubernetesResourceExpiredException(string message)
        : base(message) { }
}
