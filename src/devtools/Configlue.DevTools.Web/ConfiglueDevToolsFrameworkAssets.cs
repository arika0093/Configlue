using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Configlue.DevTools.Web;

/// <summary>
/// Serves the Blazor boot scripts for the development-only DevTools host.
/// </summary>
/// <remarks>
/// <para>
/// Development-only. The Interactive Server boot scripts
/// (<c>blazor.web.js</c> and the <c>blazor.server.js</c> chunk it imports)
/// are embedded from the build-time framework assets matching this package's
/// target framework and served from the host's own loopback endpoint, so the
/// browser UI never depends on a consumer application manifest or content
/// root. The scripts are identical for every application and carry no state;
/// they stay reachable as plain script/import loads while the document and
/// every circuit request remain session-token gated.
/// </para>
/// </remarks>
internal static class ConfiglueDevToolsFrameworkAssets
{
    internal const string WebRoutePath = "/_framework/blazor.web.js";
    internal const string ServerRoutePath = "/_framework/blazor.server.js";
    internal const string ContentType = "text/javascript; charset=utf-8";

    private static readonly Lazy<byte[]> WebPayload = new(() =>
        LoadPayload("Configlue.DevTools.Web.Framework.blazor.web.js")
    );
    private static readonly Lazy<byte[]> ServerPayload = new(() =>
        LoadPayload("Configlue.DevTools.Web.Framework.blazor.server.js")
    );

    internal static void MapFrameworkBootFiles(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet(WebRoutePath, (HttpContext context) => WriteAsync(context, WebPayload.Value));
        app.MapGet(
            ServerRoutePath,
            (HttpContext context) => WriteAsync(context, ServerPayload.Value)
        );
    }

    private static Task WriteAsync(HttpContext context, byte[] payload)
    {
        context.Response.ContentType = ContentType;
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.Body.WriteAsync(payload, context.RequestAborted).AsTask();
    }

    private static byte[] LoadPayload(string logicalName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream =
            assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"The embedded Blazor boot script '{logicalName}' was not found."
            );
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
