using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Configlue.DevTools.Web;

/// <summary>
/// Serves the DevTools Monaco overlay stylesheet for the development-only host.
/// </summary>
/// <remarks>
/// <para>
/// Development-only. The stylesheet is embedded in the package and served
/// from the host's own loopback endpoint (behind the session-token gate), so
/// it never depends on the consuming application's content root. It carries
/// no state and no secrets; the <c>link</c> tag carries the session token as
/// a query parameter.
/// </para>
/// </remarks>
internal static class ConfiglueDevToolsStyleAssets
{
    internal const string RoutePath = "/configlue-devtools.css";
    internal const string ContentType = "text/css; charset=utf-8";

    private static readonly Lazy<byte[]> Payload = new(LoadPayload);

    internal static byte[] StyleSheet => Payload.Value;

    internal static void MapStyles(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet(
            RoutePath,
            (HttpContext context) =>
            {
                context.Response.ContentType = ContentType;
                context.Response.Headers.CacheControl = "no-store";
                return context.Response.Body.WriteAsync(StyleSheet, context.RequestAborted);
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
                    candidate.EndsWith("configlue-devtools.css", StringComparison.OrdinalIgnoreCase)
                )
            ?? throw new InvalidOperationException(
                "The embedded DevTools stylesheet was not found."
            );
        using var stream =
            assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                "The embedded DevTools stylesheet could not be opened."
            );
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
