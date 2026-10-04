using Configlue.Sources;

namespace Configlue.Extensibility;

/// <summary>Declares the runtime lifetime required by a source definition's resources.</summary>
/// <remarks>
/// Definitions that create resources consuming scoped host services should implement this
/// interface and return <see cref="RuntimeLifetimeRequirement.Scoped"/>. Callers can still
/// override the declaration when registering the definition through the source registration
/// options.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueRuntimeLifetimeSource
{
    /// <summary>The dependency-injection lifetime required by this definition.</summary>
    RuntimeLifetimeRequirement RuntimeLifetime { get; }
}
