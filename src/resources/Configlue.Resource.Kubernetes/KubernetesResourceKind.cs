namespace Configlue.Resource.Kubernetes;

/// <summary>Selects which Kubernetes object kind backs a resource.</summary>
public enum KubernetesResourceKind
{
    /// <summary>A Kubernetes ConfigMap object.</summary>
    ConfigMap = 0,

    /// <summary>A Kubernetes Secret object.</summary>
    Secret = 1,
}
