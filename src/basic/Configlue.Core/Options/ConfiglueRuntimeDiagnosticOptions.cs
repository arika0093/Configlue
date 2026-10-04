namespace Configlue;

/// <summary>Controls cached runtime observations for DevTools status.</summary>
/// <remarks>
/// External observability (logging, tracing, metrics) follows standard .NET mechanisms
/// and needs no configuration here. Only the compact DevTools snapshot is opt-out.
/// Advanced observability option.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed record ConfiglueRuntimeDiagnosticOptions
{
    /// <summary>Default options: cached snapshot observations enabled.</summary>
    public static ConfiglueRuntimeDiagnosticOptions Default { get; } = new();

    /// <summary>Disables cached snapshot observations.</summary>
    public static ConfiglueRuntimeDiagnosticOptions Disabled { get; } =
        new() { TrackSnapshot = false };

    /// <summary>Whether to retain the last observed operation and source state. Enabled by default.</summary>
    public bool TrackSnapshot { get; init; } = true;

    internal void Validate()
    {
        // No constraints remain: snapshot tracking is the only knob and history was removed.
    }
}
