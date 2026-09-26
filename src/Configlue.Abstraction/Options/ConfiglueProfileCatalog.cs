namespace Configlue;

/// <summary>The persisted names and active selection for a set of Configlue profiles.</summary>
public sealed class ConfiglueProfileCatalog
{
    /// <summary>The names of the available profiles, in display order.</summary>
    public List<string> ProfileNames { get; set; } = [];

    /// <summary>The currently selected profile name.</summary>
    public string? ActiveProfileName { get; set; }
}
