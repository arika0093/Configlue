using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;

namespace Configlue.DevTools.Web;

/// <summary>
/// Root document for the development-only DevTools Blazor application.
/// </summary>
/// <remarks>
/// <para>
/// Development-only. This component renders statically (server-side, no
/// prerendered interactive content): it emits the document shell, the
/// Blazor boot scripts, and the page-memory session-token bootstrap that
/// authorizes Blazor circuit requests. All live UI renders inside
/// <see cref="ConfiglueDevToolsShell"/> over the Interactive Server circuit
/// after it connects.
/// </para>
/// <para>
/// The session token is rendered into page memory only. It is never written
/// to persisted browser storage or cookies, and the document itself is only
/// served to requests that already presented the token.
/// </para>
/// </remarks>
public sealed partial class DevToolsApp : ComponentBase
{
    private string _tokenJson = "\"\"";
    private string _blazorBootSrc = "_framework/blazor.web.js";
    private string _monacoBridgeSrc = "configlue-devtools-monaco.js";
    private string _devtoolsCssSrc = "configlue-devtools.css";

    // Versioned BlazorMonaco/Monaco library assets: identical for every app,
    // stateless, served ungated (see the token-gate public-asset carve-out)
    // because script tags, Monaco chunks, and workers cannot carry the token.
    private string _blazorMonacoInteropSrc = "_content/BlazorMonaco/jsInterop.js";
    private string _blazorMonacoLoaderSrc =
        "_content/BlazorMonaco/lib/monaco-editor/min/vs/loader.js";
    private string _blazorMonacoMainSrc =
        "_content/BlazorMonaco/lib/monaco-editor/min/vs/editor/editor.main.js";

    /// <summary>The current HTTP context, available for the statically rendered root.</summary>
    [CascadingParameter]
    public HttpContext? HttpContext { get; set; }

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        var token = ResolveToken();
        if (!string.IsNullOrEmpty(token))
        {
            _tokenJson = ConfiglueDevToolsTokenGateMiddleware.SerializeTokenForPage(token);
            var encoded = Uri.EscapeDataString(token);
            _blazorBootSrc = $"_framework/blazor.web.js?token={encoded}";
            _monacoBridgeSrc = $"configlue-devtools-monaco.js?token={encoded}";
            _devtoolsCssSrc = $"configlue-devtools.css?token={encoded}";
        }
    }

    private string? ResolveToken()
    {
        var context = HttpContext;
        if (
            context?.Items.TryGetValue(
                ConfiglueDevToolsTokenGateMiddleware.HttpItemsTokenKey,
                out var validated
            ) == true
            && validated is string gated
            && gated.Length > 0
        )
        {
            return gated;
        }

        var request = context?.Request;
        if (
            request is not null
            && ConfiglueDevToolsTokenGateMiddleware.TryExtractToken(request, out var candidate)
            && candidate.Length > 0
        )
        {
            return candidate;
        }

        return null;
    }
}
