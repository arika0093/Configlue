using Microsoft.JSInterop;

namespace Configlue.Hosting.Blazor;

/// <summary>
/// Loads <c>configlue-webstorage.js</c> through JS isolation and owns the imported module.
/// </summary>
/// <remarks>
/// The module is imported lazily on first use and the import is cached and reused for the
/// lifetime of this instance. Import, subscription, and disposal failures caused by a
/// shutting-down or disconnected JavaScript runtime are swallowed so teardown never surfaces
/// spurious failures; only <see cref="WebStorageResource"/> decides how an unavailable
/// runtime affects reads, writes, and watchers.
/// </remarks>
internal sealed class WebStorageJsModule : IAsyncDisposable
{
    internal const string ModulePath =
        "./_content/Configlue.Hosting.Blazor/configlue-webstorage.js";

    private const string MutateIdentifier = "mutate";
    private const string SubscribeIdentifier = "subscribeStorageChanges";
    private const string UnsubscribeIdentifier = "unsubscribeStorageChanges";

    private readonly IJSRuntime _jsRuntime;
    private readonly object _gate = new();
    private Task<IJSObjectReference>? _moduleTask;
    private bool _disposed;

    internal WebStorageJsModule(IJSRuntime jsRuntime)
    {
        ArgumentNullException.ThrowIfNull(jsRuntime);
        _jsRuntime = jsRuntime;
    }

    internal bool IsModuleImportStarted
    {
        get
        {
            lock (_gate)
            {
                return _moduleTask is not null;
            }
        }
    }

    internal async ValueTask<IJSObjectReference> GetModuleAsync(
        CancellationToken cancellationToken = default
    )
    {
        Task<IJSObjectReference> import;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _moduleTask ??= ImportAsync();
            import = _moduleTask;
        }

        try
        {
            return await import.ConfigureAwait(false);
        }
        catch (Exception exception)
            when (!cancellationToken.IsCancellationRequested && IsRuntimeGone(exception))
        {
            // A failed import must not poison later calls: the runtime may simply not be
            // connected yet (prerender) or may reconnect later.
            lock (_gate)
            {
                if (ReferenceEquals(_moduleTask, import))
                {
                    _moduleTask = null;
                }
            }

            throw;
        }
    }

    internal async ValueTask<string?> MutateAsync(
        string storageName,
        string key,
        string encoded,
        string? expectedRevision,
        bool mustNotExist,
        CancellationToken cancellationToken = default
    )
    {
        var module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        return await module
            .InvokeAsync<string?>(
                MutateIdentifier,
                cancellationToken,
                [storageName, key, encoded, expectedRevision, mustNotExist]
            )
            .ConfigureAwait(false);
    }

    internal async ValueTask SubscribeAsync<TReceiver>(
        string subscriptionId,
        DotNetObjectReference<TReceiver> receiver,
        CancellationToken cancellationToken = default
    )
        where TReceiver : class
    {
        var module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        await module
            .InvokeVoidAsync(SubscribeIdentifier, cancellationToken, [subscriptionId, receiver])
            .ConfigureAwait(false);
    }

    internal async ValueTask UnsubscribeAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default
    )
    {
        Task<IJSObjectReference>? import;
        lock (_gate)
        {
            import = _moduleTask;
        }

        if (import is null)
        {
            return;
        }

        IJSObjectReference module;
        try
        {
            module = await import.ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRuntimeGone(exception))
        {
            return;
        }

        try
        {
            await module
                .InvokeVoidAsync(UnsubscribeIdentifier, cancellationToken, [subscriptionId])
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRuntimeGone(exception))
        {
            // The runtime is going away; the browser-side listener dies with its realm.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task<IJSObjectReference>? import;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            import = _moduleTask;
            _moduleTask = null;
        }

        if (import is null)
        {
            return;
        }

        try
        {
            var module = await import.ConfigureAwait(false);
            await module.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Teardown is always silent: disposal races shutdown, disconnects, and failed
            // imports, none of which may surface as spurious failures.
        }
    }

    private async Task<IJSObjectReference> ImportAsync() =>
        await _jsRuntime
            .InvokeAsync<IJSObjectReference>("import", ModulePath)
            .ConfigureAwait(false);

    internal static bool IsRuntimeGone(Exception exception) =>
        exception
            is JSDisconnectedException
                or JSException
                or InvalidOperationException
                or OperationCanceledException;
}
