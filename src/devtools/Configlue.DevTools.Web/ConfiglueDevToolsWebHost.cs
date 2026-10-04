using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Configlue.DevTools.Web;

/// <summary>
/// Explicit opt-in loopback browser host for already-bound DevTools state.
/// </summary>
/// <remarks>
/// <para>
/// Development-only. Bind state instances first via <see cref="ConfiglueDevToolsRegistry"/>;
/// this host never rediscovers or re-executes application bootstrap.
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
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ConfiglueDevToolsRegistry _registry;
    private readonly string _sessionToken;
    private readonly int _requestedPort;
    private readonly bool _autoOpenBrowser;
    private TcpListener? _listener;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
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
    public bool IsRunning => _loopTask is not null && !_loopTask.IsCompleted;

    /// <summary>The base URL (loopback). Empty before <see cref="StartAsync"/>.</summary>
    public string Url => _actualPort == 0 ? string.Empty : $"http://127.0.0.1:{_actualPort}/";

    /// <summary>The session token browser clients must present. Never persisted to browser storage.</summary>
    public string SessionToken => _sessionToken;

    /// <summary>The browser launch URL including the session token. Empty before start.</summary>
    public string LaunchUrl =>
        _actualPort == 0
            ? string.Empty
            : $"http://127.0.0.1:{_actualPort}/?token={Uri.EscapeDataString(_sessionToken)}";

    /// <summary>Whether per-host browser auto-open was requested (recorded; no launch here).</summary>
    public bool AutoOpenBrowser => _autoOpenBrowser;

    /// <summary>Starts listening on loopback. No background work exists before this call.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_loopTask is not null)
        {
            throw new InvalidOperationException("The DevTools web host is already running.");
        }

        _listener = new TcpListener(IPAddress.Loopback, _requestedPort);
        _listener.Start();
        _actualPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loopCts = new CancellationTokenSource();
        _loopTask = AcceptLoopAsync(_listener, _loopCts.Token);
        return Task.CompletedTask;
    }

    /// <summary>Stops listening deterministically.</summary>
    public async Task StopAsync()
    {
        var loop = Interlocked.Exchange(ref _loopTask, null);
        if (_loopCts is not null)
        {
            try
            {
                await _loopCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException exception)
            {
                _ = exception;
            }
        }

        try
        {
            _listener?.Stop();
        }
        catch (SocketException exception)
        {
            _ = exception;
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                _ = exception;
            }
            catch (SocketException exception)
            {
                _ = exception;
            }
            catch (ObjectDisposedException exception)
            {
                _ = exception;
            }
        }

        _loopCts?.Dispose();
        _loopCts = null;
        _listener = null;
        _actualPort = 0;
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

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener
                    .AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            NetworkStream stream;
            try
            {
                stream = client.GetStream();
            }
            catch (InvalidOperationException)
            {
                return;
            }

            HttpRequest? request;
            try
            {
                request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            if (request is null)
            {
                return;
            }

            // Defense in depth: the listener is loopback-bound, but refuse non-loopback remotes.
            try
            {
                if (
                    client.Client.RemoteEndPoint is IPEndPoint remote
                    && !IPAddress.IsLoopback(remote.Address)
                )
                {
                    await WriteResponseAsync(
                            stream,
                            403,
                            "application/json; charset=utf-8",
                            """{"error":"loopback only"}""",
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    return;
                }
            }
            catch (ObjectDisposedException exception)
            {
                _ = exception;
            }
            catch (SocketException exception)
            {
                _ = exception;
            }

            try
            {
                await RouteAsync(stream, request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                _ = exception;
            }
            catch (IOException exception)
            {
                _ = exception;
            }
            catch (Exception exception)
            {
                // Development-only loopback host: never drop the connection without a
                // status. Exception messages may describe the model; secret values are
                // already redacted by the projection layer and never reach this path.
                await WriteResponseAsync(
                        stream,
                        500,
                        "application/json; charset=utf-8",
                        JsonSerializer.Serialize(
                            new { error = exception.GetType().Name, detail = exception.Message }
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task RouteAsync(
        NetworkStream stream,
        HttpRequest request,
        CancellationToken cancellationToken
    )
    {
        var path = request.Path;
        if (!IsAuthorized(request))
        {
            await WriteResponseAsync(
                    stream,
                    403,
                    "application/json; charset=utf-8",
                    """{"error":"invalid session token"}""",
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(path, "/", StringComparison.Ordinal))
        {
            var html = BuildIndexHtml();
            await WriteResponseAsync(
                    stream,
                    200,
                    "text/html; charset=utf-8",
                    html,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(path, "/api/states", StringComparison.Ordinal))
        {
            if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                await WriteMethodNotAllowedAsync(stream, cancellationToken).ConfigureAwait(false);
                return;
            }

            var states = _registry.States.Select(static state => new
            {
                modelId = state.ModelId,
                modelVersion = state.ModelVersion,
                stateName = state.StateName,
                displayName = state.DisplayName,
                modelType = state.ModelType,
            });
            await WriteResponseAsync(
                    stream,
                    200,
                    "application/json; charset=utf-8",
                    JsonSerializer.Serialize(new { states }, JsonOptions),
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }

        if (
            string.Equals(path, "/api/state", StringComparison.Ordinal)
            || string.Equals(path, "/api/schema", StringComparison.Ordinal)
            || string.Equals(path, "/api/diagnostics", StringComparison.Ordinal)
            || string.Equals(path, "/api/check", StringComparison.Ordinal)
            || string.Equals(path, "/api/viewer", StringComparison.Ordinal)
            || string.Equals(path, "/api/viewer-schema", StringComparison.Ordinal)
            || string.Equals(path, "/api/viewer-contribution", StringComparison.Ordinal)
            || string.Equals(path, "/api/save", StringComparison.Ordinal)
        )
        {
            if (
                !request.Query.TryGetValue("model", out var modelId)
                || string.IsNullOrEmpty(modelId)
            )
            {
                await WriteBadRequestAsync(stream, "missing model", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            request.Query.TryGetValue("name", out var stateName);
            stateName ??= string.Empty;
            if (!_registry.TryGet(modelId, stateName, out var entry) || entry is null)
            {
                await WriteResponseAsync(
                        stream,
                        404,
                        "application/json; charset=utf-8",
                        """{"error":"unknown state"}""",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                if (string.Equals(path, "/api/state", StringComparison.Ordinal))
                {
                    if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteMethodNotAllowedAsync(stream, cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    var json = await entry
                        .GetStateJsonAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await WriteResponseAsync(
                            stream,
                            200,
                            "application/json; charset=utf-8",
                            JsonSerializer.Serialize(
                                new
                                {
                                    modelId = entry.Info.ModelId,
                                    stateName = entry.Info.StateName,
                                    json,
                                },
                                JsonOptions
                            ),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    return;
                }

                if (string.Equals(path, "/api/schema", StringComparison.Ordinal))
                {
                    if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteMethodNotAllowedAsync(stream, cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    await WriteResponseAsync(
                            stream,
                            200,
                            "application/json; charset=utf-8",
                            entry.GetSchemaJson(),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    return;
                }

                if (string.Equals(path, "/api/diagnostics", StringComparison.Ordinal))
                {
                    if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteMethodNotAllowedAsync(stream, cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    var diagnostics = await entry
                        .GetDiagnosticsJsonAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await WriteResponseAsync(
                            stream,
                            200,
                            "application/json; charset=utf-8",
                            diagnostics,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    return;
                }

                if (string.Equals(path, "/api/viewer", StringComparison.Ordinal))
                {
                    if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteMethodNotAllowedAsync(stream, cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    long knownVersion = -1;
                    if (request.Query.TryGetValue("knownVersion", out var knownText))
                    {
                        long.TryParse(
                            knownText,
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out knownVersion
                        );
                    }

                    var delta = await entry
                        .GetViewerDeltaAsync(knownVersion, null, cancellationToken)
                        .ConfigureAwait(false);
                    var document = delta.Document;
                    await WriteResponseAsync(
                            stream,
                            200,
                            "application/json; charset=utf-8",
                            JsonSerializer.Serialize(
                                new
                                {
                                    modelId = document.ModelId,
                                    modelVersion = document.ModelVersion,
                                    stateName = entry.Info.StateName,
                                    documentVersion = document.DocumentVersion,
                                    json = delta.JsonOmitted ? string.Empty : document.Json,
                                    jsonOmitted = delta.JsonOmitted,
                                    memberRanges = document.MemberRanges,
                                    decorations = document.Decorations,
                                    hovers = document.Hovers,
                                    markers = document.Markers,
                                    schemaUri = document.SchemaUri,
                                },
                                JsonOptions
                            ),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    return;
                }

                if (string.Equals(path, "/api/viewer-schema", StringComparison.Ordinal))
                {
                    if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteMethodNotAllowedAsync(stream, cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    var setup = entry.GetViewerSchemaSetup(null);
                    await WriteResponseAsync(
                            stream,
                            200,
                            "application/json; charset=utf-8",
                            JsonSerializer.Serialize(setup, JsonOptions),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    return;
                }

                if (string.Equals(path, "/api/viewer-contribution", StringComparison.Ordinal))
                {
                    if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteMethodNotAllowedAsync(stream, cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    if (
                        !request.Query.TryGetValue("member", out var memberPath)
                        || string.IsNullOrEmpty(memberPath)
                    )
                    {
                        await WriteBadRequestAsync(stream, "missing member", cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    var contribution = await entry
                        .GetContributionJsonAsync(memberPath, null, cancellationToken)
                        .ConfigureAwait(false);
                    await WriteResponseAsync(
                            stream,
                            200,
                            "application/json; charset=utf-8",
                            contribution,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    return;
                }

                if (string.Equals(path, "/api/check", StringComparison.Ordinal))
                {
                    if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteMethodNotAllowedAsync(stream, cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    var check = await entry.RunCheckAsync(cancellationToken).ConfigureAwait(false);
                    await WriteResponseAsync(
                            stream,
                            200,
                            "application/json; charset=utf-8",
                            check,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    return;
                }

                // /api/save — the only mutating endpoint. Semantic edit-session save only.
                if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteMethodNotAllowedAsync(stream, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (string.IsNullOrEmpty(request.Body))
                {
                    await WriteBadRequestAsync(stream, "missing JSON body", cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                var result = await entry
                    .ApplyJsonAsync(request.Body, cancellationToken)
                    .ConfigureAwait(false);
                await WriteResponseAsync(
                        stream,
                        200,
                        "application/json; charset=utf-8",
                        result,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return;
            }
            catch (JsonException exception)
            {
                await WriteBadRequestAsync(
                        stream,
                        $"invalid JSON: {exception.Message}",
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return;
            }
            catch (InvalidOperationException exception)
            {
                await WriteBadRequestAsync(stream, exception.Message, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }

        await WriteResponseAsync(
                stream,
                404,
                "application/json; charset=utf-8",
                """{"error":"not found"}""",
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private bool IsAuthorized(HttpRequest request)
    {
        if (
            request.Query.TryGetValue("token", out var queryToken)
            && string.Equals(queryToken, _sessionToken, StringComparison.Ordinal)
        )
        {
            return true;
        }

        if (
            request.Headers.TryGetValue("X-Configlue-DevTools-Token", out var headerToken)
            && string.Equals(headerToken, _sessionToken, StringComparison.Ordinal)
        )
        {
            return true;
        }

        if (
            request.Headers.TryGetValue("Authorization", out var authorization)
            && authorization.StartsWith("Bearer ", StringComparison.Ordinal)
            && string.Equals(
                authorization.Substring("Bearer ".Length).Trim(),
                _sessionToken,
                StringComparison.Ordinal
            )
        )
        {
            return true;
        }

        return false;
    }

    private string BuildIndexHtml()
    {
        var tokenJson = JsonSerializer.Serialize(_sessionToken);
        var sb = new StringBuilder();
        sb.Append(
            """
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Configlue DevTools (development only)</title>
            <style>
            body{font-family:system-ui,sans-serif;margin:0;background:#111;color:#eee}
            header{background:#7a1f1f;padding:12px 16px;font-weight:700}
            main{padding:16px;max-width:960px}
            select,textarea,button{font:inherit}
            textarea{width:100%;min-height:320px;background:#1e1e1e;color:#eee;border:1px solid #555}
            .row{margin:12px 0}
            .tabs button{margin-right:8px}
            pre{background:#1e1e1e;padding:12px;overflow:auto}
            </style>
            </head>
            <body>
            <header>Configlue DevTools &mdash; development tooling only (local loopback)</header>
            <main>
            <div class="row"><label>State/model: <select id="states"></select></label></div>
            <div class="row tabs"><button id="tabState">State JSON</button><button id="tabDiagnostics">Diagnostics</button><button id="tabSchema">Schema</button></div>
            <div class="row"><textarea id="editor" spellcheck="false" placeholder="State JSON appears here. Plain fallback view; the BlazorMonaco effective-state viewer (ConfiglueEffectiveStateViewer) is available for Blazor Server UI."></textarea></div>
            <div class="row"><button id="save">Save (edit session)</button> <button id="discard">Discard</button> <button id="check">Run check</button> <span id="status"></span></div>
            <div class="row"><pre id="output"></pre></div>
            <script>
            """
        );
        sb.Append("window.__CONFIGLUE_DEVTOOLS_TOKEN__ = ");
        sb.Append(tokenJson);
        sb.Append(
            """
            ;
            // The session token lives only in page memory. It is never written to persisted
            // browser storage, cookies, or any other durable client state.
            const token = window.__CONFIGLUE_DEVTOOLS_TOKEN__;
            const headers = () => ({ 'X-Configlue-DevTools-Token': token, 'Content-Type': 'application/json' });
            const statesEl = document.getElementById('states');
            const editor = document.getElementById('editor');
            const output = document.getElementById('output');
            const status = document.getElementById('status');
            async function loadStates() {
              const res = await fetch('/api/states?token=' + encodeURIComponent(token), { headers: headers() });
              const body = await res.json();
              statesEl.innerHTML = '';
              for (const s of body.states || []) {
                const opt = document.createElement('option');
                opt.value = JSON.stringify({ model: s.modelId, name: s.stateName });
                opt.textContent = s.displayName;
                statesEl.appendChild(opt);
              }
              if (statesEl.value) await loadState();
            }
            function current() { return JSON.parse(statesEl.value || '{"model":"","name":""}'); }
            async function loadState() {
              const c = current();
              const res = await fetch('/api/state?model=' + encodeURIComponent(c.model) + '&name=' + encodeURIComponent(c.name) + '&token=' + encodeURIComponent(token), { headers: headers() });
              const body = await res.json();
              editor.value = body.json || JSON.stringify(body);
              status.textContent = 'loaded';
            }
            async function showDiagnostics() {
              const c = current();
              const res = await fetch('/api/diagnostics?model=' + encodeURIComponent(c.model) + '&name=' + encodeURIComponent(c.name) + '&token=' + encodeURIComponent(token), { headers: headers() });
              output.textContent = await res.text();
            }
            async function showSchema() {
              const c = current();
              const res = await fetch('/api/schema?model=' + encodeURIComponent(c.model) + '&name=' + encodeURIComponent(c.name) + '&token=' + encodeURIComponent(token), { headers: headers() });
              output.textContent = await res.text();
            }
            document.getElementById('discard').onclick = loadState;
            document.getElementById('tabState').onclick = loadState;
            document.getElementById('tabDiagnostics').onclick = showDiagnostics;
            document.getElementById('tabSchema').onclick = showSchema;
            statesEl.onchange = loadState;
            document.getElementById('check').onclick = async () => {
              const c = current();
              const res = await fetch('/api/check?model=' + encodeURIComponent(c.model) + '&name=' + encodeURIComponent(c.name) + '&token=' + encodeURIComponent(token), { method: 'POST', headers: headers() });
              output.textContent = await res.text();
            };
            document.getElementById('save').onclick = async () => {
              const c = current();
              const res = await fetch('/api/save?model=' + encodeURIComponent(c.model) + '&name=' + encodeURIComponent(c.name) + '&token=' + encodeURIComponent(token), { method: 'POST', headers: headers(), body: editor.value });
              output.textContent = await res.text();
              status.textContent = 'saved ' + res.status;
              await loadState();
            };
            loadStates();
            </script>
            </main>
            </body>
            </html>
            """
        );
        return sb.ToString();
    }

    private static async Task<HttpRequest?> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken
    )
    {
        var headerBytes = new List<byte>();
        var buffer = new byte[1];
        // Read until end of headers.
        while (true)
        {
            var read = await stream
                .ReadAsync(buffer, 0, 1, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            headerBytes.Add(buffer[0]);
            var count = headerBytes.Count;
            if (
                count >= 4
                && headerBytes[count - 4] == (byte)'\r'
                && headerBytes[count - 3] == (byte)'\n'
                && headerBytes[count - 2] == (byte)'\r'
                && headerBytes[count - 1] == (byte)'\n'
            )
            {
                break;
            }

            if (headerBytes.Count > 65536)
            {
                return null;
            }
        }

        var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
        var lines = headerText.Split(["\r\n"], StringSplitOptions.None);
        if (lines.Length == 0)
        {
            return null;
        }

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
        {
            return null;
        }

        var method = requestLine[0];
        var target = requestLine[1];
        var path = target;
        var queryString = string.Empty;
        var queryIndex = target.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            path = target.Substring(0, queryIndex);
            queryString = target.Substring(queryIndex + 1);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrEmpty(line))
            {
                break;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
        }

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var lengthText))
        {
            int.TryParse(lengthText, out contentLength);
        }

        string body = string.Empty;
        if (contentLength > 0)
        {
            if (contentLength > 4 * 1024 * 1024)
            {
                contentLength = 4 * 1024 * 1024;
            }

            var bodyBytes = new byte[contentLength];
            var received = 0;
            while (received < contentLength)
            {
                var read = await stream
                    .ReadAsync(bodyBytes, received, contentLength - received, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                received += read;
            }

            body = Encoding.UTF8.GetString(bodyBytes, 0, received);
        }

        return new HttpRequest(method, path, ParseQuery(queryString), headers, body);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query))
        {
            return result;
        }

        foreach (var pair in query.Split('&'))
        {
            if (string.IsNullOrEmpty(pair))
            {
                continue;
            }

            var equals = pair.IndexOf('=');
            if (equals < 0)
            {
                result[Uri.UnescapeDataString(pair)] = string.Empty;
            }
            else
            {
                result[Uri.UnescapeDataString(pair.Substring(0, equals))] = Uri.UnescapeDataString(
                    pair.Substring(equals + 1)
                );
            }
        }

        return result;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string contentType,
        string body,
        CancellationToken cancellationToken
    )
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header =
            $"HTTP/1.1 {statusCode} {ReasonPhrase(statusCode)}\r\n"
            + $"Content-Type: {contentType}\r\n"
            + $"Content-Length: {bodyBytes.Length}\r\n"
            + "Connection: close\r\n"
            + "Cache-Control: no-store\r\n"
            + "\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream
            .WriteAsync(headerBytes, 0, headerBytes.Length, cancellationToken)
            .ConfigureAwait(false);
        await stream
            .WriteAsync(bodyBytes, 0, bodyBytes.Length, cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task WriteBadRequestAsync(
        NetworkStream stream,
        string message,
        CancellationToken cancellationToken
    ) =>
        WriteResponseAsync(
            stream,
            400,
            "application/json; charset=utf-8",
            JsonSerializer.Serialize(new { error = message }, JsonOptions),
            cancellationToken
        );

    private static Task WriteMethodNotAllowedAsync(
        NetworkStream stream,
        CancellationToken cancellationToken
    ) =>
        WriteResponseAsync(
            stream,
            405,
            "application/json; charset=utf-8",
            """{"error":"method not allowed"}""",
            cancellationToken
        );

    private static string ReasonPhrase(int statusCode) =>
        statusCode switch
        {
            200 => "OK",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            500 => "Internal Server Error",
            _ => "OK",
        };

    private sealed record HttpRequest(
        string Method,
        string Path,
        Dictionary<string, string> Query,
        Dictionary<string, string> Headers,
        string Body
    );
}
