using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Configlue.DevTools.Web;

/// <summary>
/// Serves the narrow Monaco bridge script for the development-only DevTools host.
/// </summary>
/// <remarks>
/// <para>
/// Development-only. The bridge script is embedded in the package and served
/// from the host's own loopback endpoint (behind the session-token gate), so
/// it never depends on the consuming application's content root. The script
/// itself carries no state and no secrets.
/// </para>
/// </remarks>
internal static class ConfiglueDevToolsBridgeAssets
{
    internal const string RoutePath = "/configlue-devtools-monaco.js";
    internal const string ContentType = "text/javascript; charset=utf-8";

    private static readonly Lazy<byte[]> Payload = new(LoadPayload);

    internal static byte[] JavaScript => Payload.Value;

    internal static void MapBridge(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet(
            RoutePath,
            (HttpContext context) =>
            {
                context.Response.ContentType = ContentType;
                context.Response.Headers.CacheControl = "no-store";
                return context.Response.Body.WriteAsync(JavaScript, context.RequestAborted);
            }
        );
    }

    private static byte[] LoadPayload()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name =
            assembly
                .GetManifestResourceNames()
                .FirstOrDefault(static candidate =>
                    candidate.EndsWith(
                        "configlue-devtools-monaco.js",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
            ?? throw new InvalidOperationException(
                "The embedded Monaco bridge script was not found."
            );
        using var stream =
            assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                "The embedded Monaco bridge script could not be opened."
            );
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
