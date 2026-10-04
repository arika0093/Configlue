namespace Configlue.Resource.Vault;

/// <summary>The HashiCorp Vault KV secrets-engine version of a Vault path.</summary>
public enum VaultKvEngine
{
    /// <summary>KV version 1: unversioned secrets with no metadata or check-and-set.</summary>
    V1 = 1,

    /// <summary>KV version 2: versioned secrets with metadata and check-and-set writes.</summary>
    V2 = 2,
}
