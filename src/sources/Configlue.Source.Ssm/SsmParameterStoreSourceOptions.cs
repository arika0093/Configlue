using Amazon.SimpleSystemsManagement;

namespace Configlue.Source.Ssm;

/// <summary>Options for registering a Parameter Store hierarchy source.</summary>
public sealed class SsmParameterStoreSourceOptions
{
    /// <summary>An optional stable logical source ID used for provenance and explicit routing.</summary>
    public string? Id { get; init; }

    /// <summary>The root parameter path (for example <c>/myapp/prod/</c>).</summary>
    public required string RootPath { get; init; }

    /// <summary>A directly supplied client. It remains caller-owned.</summary>
    public IAmazonSimpleSystemsManagement? Client { get; init; }

    /// <summary>Resolves a client at context creation, for example from dependency injection.</summary>
    public Func<IServiceProvider?, IAmazonSimpleSystemsManagement>? ClientFactory { get; init; }

    /// <summary>Resolves a caller-owned client for each physical route.</summary>
    public Func<
        IServiceProvider?,
        RouteKey,
        IAmazonSimpleSystemsManagement
    >? ClientResolver { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>Whether this source exposes a writer. Defaults to <c>false</c> (opt-in writes).</summary>
    public bool Writable { get; init; }

    /// <summary>Whether this source exposes a polling watcher. Defaults to <c>false</c>.</summary>
    public bool WatchChanges { get; init; }

    /// <summary>Hierarchy, decryption, polling, retry, and write behavior.</summary>
    public SsmParameterStoreOptions? ResourceOptions { get; init; }
}
