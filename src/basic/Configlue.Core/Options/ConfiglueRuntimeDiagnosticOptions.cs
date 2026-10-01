namespace Configlue;

/// <summary>Controls cached runtime observations and bounded diagnostic history.</summary>
public sealed record ConfiglueRuntimeDiagnosticOptions
{
    /// <summary>Default options: cached observations enabled, event retention disabled.</summary>
    public static ConfiglueRuntimeDiagnosticOptions Default { get; } = new();

    /// <summary>Disables cached observations and event retention; explicitly subscribed listeners still receive events.</summary>
    public static ConfiglueRuntimeDiagnosticOptions Disabled { get; } =
        new() { TrackSnapshot = false };

    /// <summary>Whether to retain the last observed operation and source state. Enabled by default.</summary>
    public bool TrackSnapshot { get; init; } = true;

    /// <summary>The maximum retained event count, from zero to 4096. Zero disables history.</summary>
    public int EventHistoryCapacity { get; init; }

    internal void Validate()
    {
        if (EventHistoryCapacity is < 0 or > 4096)
        {
            throw new InvalidOperationException(
                "EventHistoryCapacity must be between zero and 4096."
            );
        }
    }
}
