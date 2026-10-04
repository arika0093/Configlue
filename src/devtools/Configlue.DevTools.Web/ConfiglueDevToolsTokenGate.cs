using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Configlue.DevTools.Web;

/// <summary>
/// Loopback session-token gate for the development-only DevTools Blazor host.
/// </summary>
/// <remarks>
/// <para>
/// Development-only. Every application request — the Blazor document, the
/// <c>/_blazor</c> circuit negotiation/websocket upgrade, and DevTools static
/// assets — must present the per-host random session token via the
/// <c>token</c> query parameter, the <c>X-Configlue-DevTools-Token</c> header,
/// or <c>Authorization: Bearer</c>. A missing or wrong token yields 403 and the
/// request never reaches the Blazor endpoint.
/// </para>
/// <para>
/// The only carve-out is the shared framework boot assets under
/// <c>/_framework/</c>, which are identical for every application, carry no
/// state, and are loaded by the browser as plain script tags (which cannot
/// attach custom headers). The initial document is still token-gated, and the
/// in-page bootstrap patches Blazor circuit requests to carry the token, so
/// the circuit cannot be negotiated without it.
/// </para>
/// <para>
/// The token lives in page memory only. It is never written to persisted
/// browser storage or cookies.
/// </para>
/// </remarks>
internal sealed class ConfiglueDevToolsTokenGateMiddleware
{
    internal const string TokenQueryName = "token";
    internal const string TokenHeaderName = "X-Configlue-DevTools-Token";
    internal const string HttpItemsTokenKey = "ConfiglueDevTools.SessionToken";

    private static readonly JsonSerializerOptions TokenJsonOptions = new();

    private readonly RequestDelegate _next;
    private readonly string _sessionToken;

    public ConfiglueDevToolsTokenGateMiddleware(RequestDelegate next, string sessionToken)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _sessionToken = string.IsNullOrEmpty(sessionToken)
            ? throw new ArgumentException("A session token is required.", nameof(sessionToken))
            : sessionToken;
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Defense in depth: the server binds loopback-only, but refuse
        // non-loopback remotes even if they reach the socket.
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && !IPAddress.IsLoopback(remote))
        {
            return WriteForbiddenAsync(context, "loopback only");
        }

        if (IsFrameworkAsset(context.Request.Path))
        {
            return _next(context);
        }

        if (!TryExtractToken(context.Request, out var candidate) || !IsAuthorized(candidate))
        {
            return WriteForbiddenAsync(context, "invalid session token");
        }

        context.Items[HttpItemsTokenKey] = _sessionToken;
        return _next(context);
    }

    internal bool IsAuthorized(string candidate) =>
        string.Equals(candidate, _sessionToken, StringComparison.Ordinal);

    internal static bool TryExtractToken(HttpRequest request, out string token)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (
            request.Query.TryGetValue(TokenQueryName, out var queryToken)
            && !string.IsNullOrEmpty(queryToken.ToString())
        )
        {
            token = queryToken.ToString();
            return true;
        }

        if (
            request.Headers.TryGetValue(TokenHeaderName, out var headerToken)
            && !string.IsNullOrEmpty(headerToken.ToString())
        )
        {
            token = headerToken.ToString();
            return true;
        }

        if (
            request.Headers.TryGetValue("Authorization", out var authorization)
            && authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal)
        )
        {
            token = authorization.ToString().Substring("Bearer ".Length).Trim();
            return !string.IsNullOrEmpty(token);
        }

        token = string.Empty;
        return false;
    }

    internal static bool IsFrameworkAsset(PathString path) =>
        path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase);

    internal static string SerializeTokenForPage(string token) =>
        JsonSerializer.Serialize(token, TokenJsonOptions);

    private static Task WriteForbiddenAsync(HttpContext context, string reason)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsync(
            JsonSerializer.Serialize(new { error = reason }, TokenJsonOptions),
            context.RequestAborted
        );
    }
}
