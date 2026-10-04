namespace Configlue.Source.Consul;

/// <summary>Configures a single-key Consul KV byte resource.</summary>
public sealed class ConsulKvResourceOptions
{
    /// <summary>Resolves the Consul key for each subject-aware operation.</summary>
    /// <remarks>Selectors should return stable values for a given context.</remarks>
    public Func<ConfiglueResourceContext, string>? KeySelector { get; init; }

    /// <summary>Resolves the datacenter for each subject-aware operation.</summary>
    public Func<ConfiglueResourceContext, string?>? DatacenterSelector { get; init; }

    /// <summary>Resolves the namespace for each subject-aware operation.</summary>
    public Func<ConfiglueResourceContext, string?>? NamespaceSelector { get; init; }

    /// <summary>Resolves the partition for each subject-aware operation.</summary>
    public Func<ConfiglueResourceContext, string?>? PartitionSelector { get; init; }

    /// <summary>The datacenter to use when no selector overrides it.</summary>
    public string? Datacenter { get; init; }

    /// <summary>The Consul namespace to use when no selector overrides it.</summary>
    public string? Namespace { get; init; }

    /// <summary>The Consul partition to use when no selector overrides it.</summary>
    public string? Partition { get; init; }

    /// <summary>The read consistency mode.</summary>
    public ConsulConsistencyMode Consistency { get; init; } = ConsulConsistencyMode.Default;

    /// <summary>How long a blocking watch query may wait. Defaults to five minutes.</summary>
    public TimeSpan BlockingWaitTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Resolves an externally owned client for each subject-aware operation.</summary>
    public Func<ConfiglueResourceContext, IConsulKvClient>? ClientSelector { get; init; }

    /// <summary>
    /// An advanced fixed identity override shared by every operation context. Configuring this
    /// asserts that all selected keys share one physical coordination domain; an
    /// incorrect value can make batch grouping unsafe.
    /// </summary>
    public ResourceId? FixedResourceId { get; init; }
}

/// <summary>Configures the key prefix and Consul routing of a prefix source.</summary>
/// <remarks>
/// <para>Deterministic prefix mapping:</para>
/// <list type="bullet">
/// <item><description>The prefix is normalized by trimming trailing <c>/</c> characters.</description></item>
/// <item><description>Each key under <c>prefix + "/"</c> is split on <c>/</c> into segments.</description></item>
/// <item><description>Segments resolve case-insensitively against generated member names using the model's schema; no reflection is used.</description></item>
/// <item><description>Intermediate segments must name nested models; a key that continues past a scalar member is malformed.</description></item>
/// <item><description>A key that names a nested model itself (without leaf segments) is malformed.</description></item>
/// <item><description>Absent keys mean absent members; a missing prefix reads as <c>NotFound</c>.</description></item>
/// <item><description>Two keys that resolve to the same member path conflict and fail the read.</description></item>
/// <item><description>Ambiguous segments (two members differing only by case) fail the read.</description></item>
/// </list>
/// </remarks>
public sealed class ConsulKvPrefixOptions
{
    /// <summary>Resolves the key prefix for each subject-aware operation.</summary>
    /// <remarks>Selectors should return stable values for a given context.</remarks>
    public Func<ConfiglueResourceContext, string>? KeyPrefixSelector { get; init; }

    /// <summary>Resolves the datacenter for each subject-aware operation.</summary>
    public Func<ConfiglueResourceContext, string?>? DatacenterSelector { get; init; }

    /// <summary>Resolves the namespace for each subject-aware operation.</summary>
    public Func<ConfiglueResourceContext, string?>? NamespaceSelector { get; init; }

    /// <summary>Resolves the partition for each subject-aware operation.</summary>
    public Func<ConfiglueResourceContext, string?>? PartitionSelector { get; init; }

    /// <summary>The datacenter to use when no selector overrides it.</summary>
    public string? Datacenter { get; init; }

    /// <summary>The Consul namespace to use when no selector overrides it.</summary>
    public string? Namespace { get; init; }

    /// <summary>The Consul partition to use when no selector overrides it.</summary>
    public string? Partition { get; init; }

    /// <summary>The read consistency mode.</summary>
    public ConsulConsistencyMode Consistency { get; init; } = ConsulConsistencyMode.Default;

    /// <summary>How long a blocking watch query may wait. Defaults to five minutes.</summary>
    public TimeSpan BlockingWaitTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Whether multi-key writes use the atomic transaction API. Defaults to true.</summary>
    public bool UseTransaction { get; init; } = true;

    /// <summary>
    /// An advanced fixed identity override shared by every operation context. Configuring this
    /// asserts that all selected prefixes share one physical coordination domain; an
    /// incorrect value can make batch grouping unsafe.
    /// </summary>
    public ResourceId? FixedResourceId { get; init; }

    internal void Validate()
    {
        if (BlockingWaitTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("A Consul blocking wait timeout must be positive.");
        }
    }
}
