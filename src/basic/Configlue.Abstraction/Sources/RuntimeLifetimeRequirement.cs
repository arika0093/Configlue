namespace Configlue.Sources;

/// <summary>
/// Declares the dependency-injection lifetime that a state source requires for its runtime.
/// </summary>
/// <remarks>
/// Ordinary file, database, and object-store sources can be shared, so a model whose complete
/// source topology is shareable keeps the shared Configlue runtime. A source that consumes a
/// scoped host dependency, such as a circuit-scoped browser JavaScript runtime, must declare
/// <see cref="Scoped"/> so its runtime is created and disposed with the dependency-injection scope
/// instead of becoming a captive dependency.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public enum RuntimeLifetimeRequirement
{
    /// <summary>The source can be shared; the runtime may be a singleton.</summary>
    Shared = 0,

    /// <summary>
    /// The source consumes scoped host services; its runtime must be created and disposed per
    /// dependency-injection scope.
    /// </summary>
    Scoped = 1,
}

/// <summary>Combines runtime lifetime requirements for a complete source topology.</summary>
/// <remarks>Advanced composition helper.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public static class RuntimeLifetimeRequirementExtensions
{
    /// <summary>
    /// Combines two requirements. The result is <see cref="RuntimeLifetimeRequirement.Scoped"/>
    /// when either input is scoped.
    /// </summary>
    public static RuntimeLifetimeRequirement Combine(
        this RuntimeLifetimeRequirement left,
        RuntimeLifetimeRequirement right
    ) =>
        left == RuntimeLifetimeRequirement.Scoped || right == RuntimeLifetimeRequirement.Scoped
            ? RuntimeLifetimeRequirement.Scoped
            : RuntimeLifetimeRequirement.Shared;

    /// <summary>Combines a sequence of requirements into one requirement.</summary>
    public static RuntimeLifetimeRequirement Combine(
        this IEnumerable<RuntimeLifetimeRequirement> requirements
    )
    {
        ArgumentNullException.ThrowIfNull(requirements);
        var combined = RuntimeLifetimeRequirement.Shared;
        foreach (var requirement in requirements)
        {
            combined = combined.Combine(requirement);
        }

        return combined;
    }
}
