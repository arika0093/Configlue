using Microsoft.JSInterop;

namespace Configlue.Hosting.Blazor;

/// <summary>
/// Receives browser <c>storage</c> events from <c>configlue-webstorage.js</c>.
/// </summary>
/// <remarks>
/// Kept internal: JavaScript only needs the <c>OnStorageChanged</c> callback name. The receiver
/// forwards to its owning resource, which fans out to the waiters whose resolved storage
/// address matches. Notifications are level-triggered hints meaning "re-read"; no state is
/// pushed through the bridge.
/// </remarks>
internal sealed class WebStorageChangeReceiver
{
    private readonly WebStorageResource _owner;

    internal WebStorageChangeReceiver(WebStorageResource owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    /// <summary>Invoked by JavaScript when a browser storage event fires.</summary>
    /// <param name="storageName">
    /// <c>localStorage</c>, <c>sessionStorage</c>, or <c>null</c> when the area is unknown.
    /// </param>
    /// <param name="key">The changed storage key, or <c>null</c> for <c>clear()</c>.</param>
    [JSInvokable]
    public void OnStorageChanged(string? storageName, string? key) =>
        _owner.NotifyStorageEvent(storageName, key);
}
