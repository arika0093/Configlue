namespace Configlue.Source.Consul;

/// <summary>Consistency mode for Consul KV reads.</summary>
public enum ConsulConsistencyMode
{
    /// <summary>Default Consul consistency (leader-verified on first read).</summary>
    Default = 0,

    /// <summary>Allow stale reads from any server.</summary>
    Stale = 1,

    /// <summary>Require a leader-verified consistent read.</summary>
    Consistent = 2,
}
