namespace Configlue;

/// <summary>Reports the runtime lifetime actually used by a state runtime.</summary>
internal interface IConfiglueRuntimeLifetimeProvider
{
    /// <summary>The combined lifetime requirement of the runtime's source topology.</summary>
    RuntimeLifetimeRequirement RuntimeLifetime { get; }
}
