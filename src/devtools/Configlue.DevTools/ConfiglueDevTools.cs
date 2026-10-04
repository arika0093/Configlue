using System.Net;

namespace Configlue.DevTools;

/// <summary>
/// Host-neutral DevTools session pointer plus browser-launch helper.
/// </summary>
/// <remarks>
/// <para>
/// Development-only. Referencing a hosting package never enables DevTools:
/// <see cref="IsEnabled"/> starts <c>false</c> and no server is started here.
/// The application explicitly opts in with <see cref="Enable"/> after starting
/// its own loopback <c>ConfiglueDevToolsWebHost</c> (see <c>Configlue.DevTools.Web</c>).
/// </para>
/// <para>
/// Launch helpers never start servers, so multiple opens reuse the same URL and
/// can never start duplicate servers accidentally. They never log the launch URL
/// or session token.
/// </para>
/// </remarks>
public static class ConfiglueDevTools
{
    private static readonly object Gate = new();
    private static bool _enabled;
    private static string? _launchUrl;

    /// <summary>Whether browser launch is explicitly enabled. Starts <c>false</c>.</summary>
    public static bool IsEnabled
    {
        get
        {
            lock (Gate)
            {
                return _enabled;
            }
        }
    }

    /// <summary>The active loopback launch URL including the session token, if published.</summary>
    public static string? CurrentLaunchUrl
    {
        get
        {
            lock (Gate)
            {
                return _launchUrl;
            }
        }
    }

    /// <summary>
    /// Explicitly enables browser launch for the supplied loopback launch URL.
    /// </summary>
    /// <param name="launchUrl">The running host launch URL including the session token.</param>
    /// <exception cref="ArgumentException">The URL is empty or is not a loopback HTTP URL.</exception>
    public static void Enable(string launchUrl)
    {
        if (launchUrl is null || launchUrl.Trim().Length == 0)
        {
            throw new ArgumentException("A DevTools launch URL is required.", nameof(launchUrl));
        }

        if (!IsLoopbackHttpUrl(launchUrl))
        {
            throw new ArgumentException(
                "The DevTools launch URL must be a loopback HTTP URL.",
                nameof(launchUrl)
            );
        }

        lock (Gate)
        {
            _launchUrl = launchUrl;
            _enabled = true;
        }
    }

    /// <summary>Disables browser launch. The published URL is kept for diagnostics.</summary>
    public static void Disable()
    {
        lock (Gate)
        {
            _enabled = false;
        }
    }

    /// <summary>Clears the published launch URL (for example on application shutdown).</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            _launchUrl = null;
        }
    }

    /// <summary>
    /// Opens the active session URL in the system browser. Returns <c>false</c>
    /// without launching when disabled or when no URL is published.
    /// </summary>
    /// <remarks>
    /// DevTools-owned default launcher (<see cref="SystemBrowserLauncher"/>); no
    /// host-specific wrapper is required for ordinary desktop/web hosts.
    /// </remarks>
    public static Task<bool> OpenBrowserAsync(CancellationToken cancellationToken = default) =>
        OpenBrowserAsync(new SystemBrowserLauncher(), cancellationToken);

    /// <summary>
    /// Opens an explicit launch URL in the system browser. Returns <c>false</c>
    /// without launching when the URL is empty. Explicit URLs bypass the global
    /// <see cref="IsEnabled"/> flag because passing a URL is itself explicit opt-in.
    /// </summary>
    /// <exception cref="ArgumentException">The non-empty URL is not a loopback HTTP URL.</exception>
    public static Task<bool> OpenBrowserAsync(
        string? launchUrl,
        CancellationToken cancellationToken = default
    ) => OpenBrowserAsync(launchUrl, new SystemBrowserLauncher(), cancellationToken);

    /// <summary>
    /// Opens the active session URL when enabled. Returns <c>false</c> without
    /// invoking the launcher when disabled or when no URL is published.
    /// </summary>
    public static Task<bool> OpenBrowserAsync(
        IConfiglueDevToolsBrowserLauncher launcher,
        CancellationToken cancellationToken = default
    )
    {
        if (launcher is null)
        {
            throw new ArgumentNullException(nameof(launcher));
        }

        string? url;
        lock (Gate)
        {
            if (!_enabled)
            {
                return Task.FromResult(false);
            }

            url = _launchUrl;
        }

        if (url is null || url.Trim().Length == 0)
        {
            return Task.FromResult(false);
        }

        return OpenUrlAsync(url, launcher, cancellationToken);
    }

    /// <summary>
    /// Opens an explicit launch URL. Returns <c>false</c> without invoking the
    /// launcher when the URL is empty. Explicit URLs bypass the global
    /// <see cref="IsEnabled"/> flag because passing a URL is itself explicit opt-in.
    /// </summary>
    /// <exception cref="ArgumentException">The non-empty URL is not a loopback HTTP URL.</exception>
    public static Task<bool> OpenBrowserAsync(
        string? launchUrl,
        IConfiglueDevToolsBrowserLauncher launcher,
        CancellationToken cancellationToken = default
    )
    {
        if (launcher is null)
        {
            throw new ArgumentNullException(nameof(launcher));
        }

        if (launchUrl is null || launchUrl.Trim().Length == 0)
        {
            return Task.FromResult(false);
        }

        if (!IsLoopbackHttpUrl(launchUrl))
        {
            throw new ArgumentException(
                "The DevTools launch URL must be a loopback HTTP URL.",
                nameof(launchUrl)
            );
        }

        return OpenUrlAsync(launchUrl, launcher, cancellationToken);
    }

    /// <summary>
    /// Function-based overload of <see cref="OpenBrowserAsync(IConfiglueDevToolsBrowserLauncher, CancellationToken)"/>.
    /// </summary>
    public static Task<bool> OpenBrowserAsync(
        Func<string, CancellationToken, Task> openAsync,
        CancellationToken cancellationToken = default
    )
    {
        if (openAsync is null)
        {
            throw new ArgumentNullException(nameof(openAsync));
        }

        return OpenBrowserAsync(new FuncLauncher(openAsync), cancellationToken);
    }

    /// <summary>
    /// Function-based overload of <see cref="OpenBrowserAsync(string?, IConfiglueDevToolsBrowserLauncher, CancellationToken)"/>.
    /// </summary>
    public static Task<bool> OpenBrowserAsync(
        string? launchUrl,
        Func<string, CancellationToken, Task> openAsync,
        CancellationToken cancellationToken = default
    )
    {
        if (openAsync is null)
        {
            throw new ArgumentNullException(nameof(openAsync));
        }

        return OpenBrowserAsync(launchUrl, new FuncLauncher(openAsync), cancellationToken);
    }

    private static async Task<bool> OpenUrlAsync(
        string url,
        IConfiglueDevToolsBrowserLauncher launcher,
        CancellationToken cancellationToken
    )
    {
        await launcher.OpenAsync(url, cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal static bool IsLoopbackHttpUrl(string? url)
    {
        if (url is null || url.Trim().Length == 0)
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var trimmed = host.Trim('[', ']');
        if (IPAddress.TryParse(trimmed, out var address))
        {
            return IPAddress.IsLoopback(address);
        }

        return false;
    }

    private sealed class FuncLauncher(Func<string, CancellationToken, Task> openAsync)
        : IConfiglueDevToolsBrowserLauncher
    {
        public Task OpenAsync(string url, CancellationToken cancellationToken = default) =>
            openAsync(url, cancellationToken);
    }
}
