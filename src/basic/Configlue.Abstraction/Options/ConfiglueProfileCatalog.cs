namespace Configlue;

/// <summary>
/// The persisted membership and active selection for a set of Configlue profiles.
/// </summary>
/// <remarks>
/// The catalog is the source of truth for which named states are profiles and which one is active.
/// Profile logical identity is <c>(TModel, StateName)</c> and profile names share the same logical
/// state-name namespace as fixed and dynamic named states.
/// </remarks>
public sealed class ConfiglueProfileCatalog
{
    /// <summary>The names of the available profiles, in logical state-name order.</summary>
    public List<string> ProfileNames { get; set; } = [];

    /// <summary>The currently selected profile name.</summary>
    public string? ActiveProfileName { get; set; }
}
