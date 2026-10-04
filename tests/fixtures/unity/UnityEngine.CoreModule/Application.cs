namespace UnityEngine;

/// <summary>Minimal deterministic stand-in for the Unity player application.</summary>
public static class Application
{
    /// <summary>The application-specific persistent-data root.</summary>
    public static string persistentDataPath { get; set; } = string.Empty;

    /// <summary>Cancelled when the current play session or player exits.</summary>
    public static CancellationToken exitCancellationToken { get; set; } = CancellationToken.None;

    /// <summary>URLs opened through the system browser. Test double records launches.</summary>
    public static List<string> OpenedUrls { get; } = [];

    /// <summary>Minimal stand-in for Unity's system-browser launch.</summary>
    public static void OpenURL(string url) => OpenedUrls.Add(url);
}
