namespace UnityEngine;

/// <summary>Minimal deterministic stand-in for the Unity player application.</summary>
public static class Application
{
    /// <summary>The application-specific persistent-data root.</summary>
    public static string persistentDataPath { get; set; } = string.Empty;

    /// <summary>Cancelled when the current play session or player exits.</summary>
    public static CancellationToken exitCancellationToken { get; set; } = CancellationToken.None;
}
