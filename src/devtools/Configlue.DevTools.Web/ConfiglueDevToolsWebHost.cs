using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Configlue.DevTools.Web;

/// <summary>
/// Explicit opt-in loopback Blazor host for already-bound DevTools state.
/// </summary>
/// <remarks>
/// <para>
/// Development-only. Bind state instances first via <see cref="ConfiglueDevToolsRegistry"/>.
/// This host never rediscovers or re-executes application bootstrap. The browser
/// application is a real Blazor Web App using Interactive Server (no
/// prerendering): the BlazorMonaco effective-state editor and the
/// diagnostics/statistics tab operate directly on the in-process runtime via
/// the registered <see cref="ConfiglueDevToolsRegistry"/>. No DevTools-specific
/// REST transport is required for the local UI.
/// </para>
/// <para>Non-DI usage:</para>
/// <code>
/// var registry = new ConfiglueDevToolsRegistry();
/// registry.Add(context.GetState&lt;AppSettings&gt;());
/// await using var host = ConfiglueDevToolsWebHost.Create(registry);
/// await host.StartAsync();
/// // Open host.LaunchUrl in a browser.
/// </code>
/// <para>
/// The host does no background work until <see cref="StartAsync"/> runs. Disposal is
/// deterministic via <see cref="StopAsync"/>, <see cref="Dispose"/>, or
/// <see cref="DisposeAsync"/>.
/// </para>
/// </remarks>
public sealed class ConfiglueDevToolsWebHost : IAsyncDisposable, IDisposable
{
    private readonly ConfiglueDevToolsRegistry _registry;
    private readonly string _sessionToken;
    private readonly int _requestedPort;
    private readonly bool _autoOpenBrowser;
    private readonly object _gate = new();
    private WebApplication? _app;
    private int _actualPort;
    private int _disposed;

    private ConfiglueDevToolsWebHost(
        ConfiglueDevToolsRegistry registry,
        int port,
        string sessionToken,
        bool autoOpenBrowser
    )
    {
        _registry = registry;
        _requestedPort = port;
        _sessionToken = sessionToken;
        _autoOpenBrowser = autoOpenBrowser;
    }

    /// <summary>Creates a host bound to the supplied live registry. Does not start listening.</summary>
    public static ConfiglueDevToolsWebHost Create(
        ConfiglueDevToolsRegistry registry,
        ConfiglueDevToolsWebOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(registry);
        options ??= new ConfiglueDevToolsWebOptions();
        options.Validate();
        var token = string.IsNullOrEmpty(options.SessionToken)
            ? GenerateSessionToken()
            : options.SessionToken;
        return new ConfiglueDevToolsWebHost(registry, options.Port, token, options.AutoOpenBrowser);
    }

    /// <summary>Whether the listener is running.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _app is not null;
            }
        }
    }

    /// <summary>The base URL (loopback). Empty before <see cref="StartAsync"/>.</summary>
    public string Url
    {
        get
        {
            var port = Volatile.Read(ref _actualPort);
            return port == 0 ? string.Empty : $"http://127.0.0.1:{port}/";
        }
    }

    /// <summary>The session token browser clients must present. Never persisted to browser storage.</summary>
    public string SessionToken => _sessionToken;

    /// <summary>The browser launch URL including the session token. Empty before start.</summary>
    public string LaunchUrl
    {
        get
        {
            var port = Volatile.Read(ref _actualPort);
            return port == 0
                ? string.Empty
                : $"http://127.0.0.1:{port}/?token={Uri.EscapeDataString(_sessionToken)}";
        }
    }

    /// <summary>Whether per-host browser auto-open was requested (recorded; no launch here).</summary>
    public bool AutoOpenBrowser => _autoOpenBrowser;

    /// <summary>
    /// The host service provider once started. Internal test hook proving the live
    /// registry is registered directly into the Blazor host.
    /// </summary>
    internal IServiceProvider? Services
    {
        get
        {
            lock (_gate)
            {
                return _app?.Services;
            }
        }
    }

    /// <summary>Starts the loopback Blazor host. No background work exists before this call.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_gate)
        {
            if (_app is not null)
            {
                throw new InvalidOperationException("The DevTools web host is already running.");
            }
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{_requestedPort}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(_registry);
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        var app = builder.Build();
        app.UseMiddleware<ConfiglueDevToolsTokenGateMiddleware>(_sessionToken);
        app.UseAntiforgery();
        app.MapBridge();
        app.MapFrameworkBootFiles();
        app.MapRazorComponents<DevToolsApp>().AddInteractiveServerRenderMode();

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var port = ResolveLoopbackPort(app);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                _app = app;
                Volatile.Write(ref _actualPort, port);
            }
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Stops the host deterministically.</summary>
    public async Task StopAsync()
    {
        WebApplication? app;
        lock (_gate)
        {
            app = _app;
            _app = null;
            Volatile.Write(ref _actualPort, 0);
        }

        if (app is null)
        {
            return;
        }

        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopAsync().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
    }

    internal static string GenerateSessionToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static int ResolveLoopbackPort(WebApplication app)
    {
        foreach (var url in app.Urls)
        {
            if (
                Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && IsLoopbackHost(uri.Host)
                && !uri.IsDefaultPort
            )
            {
                return uri.Port;
            }
        }

        throw new InvalidOperationException(
            "The DevTools web host did not report a loopback address after starting."
        );
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var trimmed = host.Trim('[', ']');
        return IPAddress.TryParse(trimmed, out var address) && IPAddress.IsLoopback(address);
    }
}
