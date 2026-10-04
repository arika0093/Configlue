using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Configlue.Resources;
using Configlue.Sources;
using Microsoft.JSInterop;

namespace Configlue.Hosting.Blazor;

/// <summary>Selects which browser storage area backs a WebStorage resource.</summary>
public enum WebStorageKind
{
    /// <summary>The <c>localStorage</c> area, shared across tabs of one origin.</summary>
    Local,

    /// <summary>The <c>sessionStorage</c> area, scoped to one browser tab.</summary>
    Session,
}

/// <summary>Thrown when browser storage cannot be reached, for example during prerender.</summary>
public sealed class WebStorageUnavailableException : InvalidOperationException
{
    /// <summary>Creates the exception with a reason.</summary>
    public WebStorageUnavailableException(string message)
        : base(message) { }

    /// <summary>Creates the exception with a reason and inner cause.</summary>
    public WebStorageUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Thrown when a conditional write cannot be enforced atomically on the current browser.
/// </summary>
/// <remarks>
/// The Web Locks API is required to serialize the read/compare/write critical section across
/// browser execution contexts. When it is unavailable the resource rejects conditional writes
/// instead of emulating compare-and-swap with a racy read-then-write. Unconditional writes remain
/// available.
/// </remarks>
public sealed class WebStorageAtomicityNotSupportedException : NotSupportedException
{
    /// <summary>Creates the exception with a reason.</summary>
    public WebStorageAtomicityNotSupportedException(string message)
        : base(message) { }

    /// <summary>Creates the exception with a reason and inner cause.</summary>
    public WebStorageAtomicityNotSupportedException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Reads and writes one logical value through browser <c>localStorage</c> or <c>sessionStorage</c>.
/// </summary>
/// <remarks>
/// <para>
/// The resource uses the scoped <see cref="IJSRuntime"/> and must therefore be created for a
/// dependency-injection scope. During Blazor prerender, or after a Blazor Server circuit
/// disconnects, JavaScript is unavailable; reads report <see cref="StateReadStatus.Unavailable"/>
/// and writes throw <see cref="WebStorageUnavailableException"/> instead of silently using a
/// different storage.
/// </para>
/// <para>
/// The browser helper module (<c>configlue-webstorage.js</c>) is loaded through JS isolation on
/// first use; no host-page <c>script</c> tag is required. The import is cached and reused, and
/// module disposal is deterministic through <see cref="DisposeAsync"/>. When the JavaScript
/// runtime is shutting down or disconnected, teardown stays silent instead of surfacing
/// spurious failures.
/// </para>
/// <para>
/// Conditional writes (<see cref="RevisionCondition.Match"/> and
/// <see cref="RevisionCondition.MustNotExist"/>) are performed as a single browser-side mutation.
/// The browser helper takes the browser-wide Web Locks exclusive lock named for the storage area
/// and resolved key, then reads the current envelope, validates the precondition, and writes the
/// new envelope inside one <c>navigator.locks.request</c> callback. Returning from the callback
/// releases the lock, so the lock lifetime is bound to the mutation itself rather than to a
/// timeout that could expire while a commit is still possible. This coordinates every cooperating
/// writer for that key regardless of tab, window, iframe, Blazor Server circuit, WebAssembly
/// realm, or component scope. Two writers that observed the same revision therefore cannot both
/// commit; the losing writer receives <see cref="StateConflictException"/>.
/// </para>
/// <para>
/// The Web Locks API is required for conditional writes. When it is missing, conditional writes
/// throw <see cref="WebStorageAtomicityNotSupportedException"/> rather than falling back to a
/// non-atomic read-then-write. Unconditional writes do not take the lock and keep their
/// last-writer-wins semantics.
/// </para>
/// <para>
/// The resource is also an <see cref="ISourceWatcher"/>. Browser <c>storage</c> events wake
/// matching waiters, and every successful same-context write explicitly signals local waiters
/// (the browser event does not fire in the writing context). For
/// <see cref="WebStorageKind.Session"/>, browser scoping is respected: no cross-tab propagation
/// is assumed. Notifications are level-triggered "re-read" hints filtered by the exact resolved
/// storage address, so duplicate signals are harmless and a missed transient can only delay, not
/// corrupt, convergence.
/// </para>
/// </remarks>
public sealed class WebStorageResource
    : IResourceReader,
        IResourceWriter,
        ISourceWatcher,
        IAsyncDisposable
{
    private static readonly JsonSerializerOptions EnvelopeOptions = new(JsonSerializerDefaults.Web);
    private readonly IJSRuntime _jsRuntime;
    private readonly WebStorageJsModule _jsModule;
    private readonly Func<ConfiglueResourceContext, string>? _keySelector;
    private readonly bool _watchChanges;
    private readonly object _watchGate = new();
    private readonly Dictionary<WatchAddress, List<ChangeWaiter>> _waiters = new();
    private readonly string _subscriptionId = Guid.NewGuid().ToString("N");
    private DotNetObjectReference<WebStorageChangeReceiver>? _receiver;
    private Task<bool>? _subscribeTask;
    private bool _subscribed;
    private int _disposed;

    /// <summary>Creates a resource over one browser storage area and key.</summary>
    public WebStorageResource(
        IJSRuntime jsRuntime,
        WebStorageKind kind,
        string key,
        Func<ConfiglueResourceContext, string>? keySelector = null
    )
        : this(jsRuntime, kind, key, keySelector, watchChanges: true) { }

    /// <summary>Creates a resource over one browser storage area and key.</summary>
    /// <param name="jsRuntime">The scoped JavaScript runtime.</param>
    /// <param name="kind">The browser storage area backing this resource.</param>
    /// <param name="key">The base storage key.</param>
    /// <param name="keySelector">An optional selector overriding the resolved storage key.</param>
    /// <param name="watchChanges">
    /// Whether <see cref="WaitForChangeAsync"/> registers a browser <c>storage</c> listener.
    /// When <c>false</c>, waits never complete until canceled and no JavaScript subscription
    /// is created. Source registration normally passes a null watcher instead, so this flag
    /// primarily governs direct resource use.
    /// </param>
    public WebStorageResource(
        IJSRuntime jsRuntime,
        WebStorageKind kind,
        string key,
        Func<ConfiglueResourceContext, string>? keySelector,
        bool watchChanges
    )
    {
        ArgumentNullException.ThrowIfNull(jsRuntime);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _jsRuntime = jsRuntime;
        _jsModule = new WebStorageJsModule(jsRuntime);
        Kind = kind;
        Key = key;
        _keySelector = keySelector;
        _watchChanges = watchChanges;
    }

    /// <summary>The browser storage area backing this resource.</summary>
    public WebStorageKind Kind { get; }

    /// <summary>The base storage key.</summary>
    public string Key { get; }

    internal bool IsSubscribedForTests
    {
        get
        {
            lock (_watchGate)
            {
                return _subscribed;
            }
        }
    }

    internal int WaiterCountForTests
    {
        get
        {
            lock (_watchGate)
            {
                var count = 0;
                foreach (var entry in _waiters.Values)
                {
                    count += entry.Count;
                }

                return count;
            }
        }
    }

    private string StorageName => Kind == WebStorageKind.Local ? "localStorage" : "sessionStorage";

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var raw = await _jsRuntime
                .InvokeAsync<string?>(
                    StorageName + ".getItem",
                    cancellationToken,
                    [ResolveKey(context)]
                )
                .ConfigureAwait(false);
            return string.IsNullOrEmpty(raw) ? ResourceReadResult.NotFound() : Decode(raw);
        }
        catch (Exception exception) when (IsUnavailable(exception, cancellationToken))
        {
            return ResourceReadResult.Unavailable();
        }
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var key = ResolveKey(context);
        var revision = Guid.NewGuid().ToString("N");
        var encoded = Encode(revision, request.Content);
        try
        {
            if (request.Condition.IsNone)
            {
                await _jsRuntime
                    .InvokeVoidAsync(StorageName + ".setItem", cancellationToken, [key, encoded])
                    .ConfigureAwait(false);
                SignalLocalWatchers(key);
                return new StateWriteResult(revision);
            }

            var status = await MutateAsync(key, encoded, request.Condition, cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(status, "committed", StringComparison.Ordinal))
            {
                SignalLocalWatchers(key);
                return new StateWriteResult(revision);
            }

            if (string.Equals(status, "conflict", StringComparison.Ordinal))
            {
                throw new StateConflictException(
                    $"The browser storage key '{key}' does not satisfy the requested revision condition."
                );
            }

            if (string.Equals(status, "unavailable", StringComparison.Ordinal))
            {
                throw new JSException("The browser storage mutation failed.");
            }

            throw new WebStorageAtomicityNotSupportedException(
                $"The browser does not expose the Web Locks API, so the browser storage key '{key}' cannot be written atomically under a revision precondition. "
                    + "Unconditional writes remain available."
            );
        }
        catch (Exception exception) when (IsUnavailable(exception, cancellationToken))
        {
            throw new WebStorageUnavailableException(
                "Browser storage is not available. This can happen during prerender or after a Blazor Server circuit disconnects.",
                exception
            );
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Level-triggered: the current revision is compared first so an already-changed value
    /// returns immediately, then the waiter is registered before waiting so a concurrent
    /// same-context write or browser event cannot slip between the check and the wait. When
    /// JavaScript is unavailable (prerender, disconnect) the wait only ends on cancellation.
    /// </remarks>
    public async ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var key = ResolveKey(context);
        if (!_watchChanges)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!await EnsureSubscribedAsync(cancellationToken).ConfigureAwait(false))
        {
            // A false result with no disposal means JavaScript is unavailable (prerender,
            // disconnect): nothing can signal us, so wait for cancellation only.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return;
        }

        var waiter = new ChangeWaiter();
        var address = new WatchAddress(Kind, key);
        lock (_watchGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!_waiters.TryGetValue(address, out var bucket))
            {
                bucket = [];
                _waiters.Add(address, bucket);
            }

            bucket.Add(waiter);
        }

        try
        {
            var current = await ReadAsync(context, cancellationToken).ConfigureAwait(false);
            if (
                current.Status == StateReadStatus.Unavailable
                || !string.Equals(current.Revision, observedRevision, StringComparison.Ordinal)
            )
            {
                return;
            }

            await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_watchGate)
            {
                if (_waiters.TryGetValue(address, out var bucket))
                {
                    bucket.Remove(waiter);
                    if (bucket.Count == 0)
                    {
                        _waiters.Remove(address);
                    }
                }
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        List<ChangeWaiter> pending = [];
        DotNetObjectReference<WebStorageChangeReceiver>? receiver;
        Task<bool>? subscribeTask;
        lock (_watchGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            foreach (var bucket in _waiters.Values)
            {
                pending.AddRange(bucket);
            }

            _waiters.Clear();
            receiver = _receiver;
            _receiver = null;
            subscribeTask = _subscribeTask;
            _subscribeTask = null;
        }

        foreach (var waiter in pending)
        {
            waiter.TrySetCanceled();
        }

        if (subscribeTask is not null)
        {
            try
            {
                await subscribeTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Teardown observes, never reports.
            }
        }

        if (receiver is not null)
        {
            try
            {
                await _jsModule.UnsubscribeAsync(_subscriptionId).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Disconnect-safe by contract; belt and suspenders for fakes.
            }

            receiver.Dispose();
        }

        await _jsModule.DisposeAsync().ConfigureAwait(false);
    }

    internal void NotifyStorageEvent(string? storageName, string? key)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        CompleteWhere(address =>
            StorageNameMatches(storageName, address.Kind)
            && (key is null || string.Equals(key, address.Key, StringComparison.Ordinal))
        );
    }

    private void SignalLocalWatchers(string key)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        CompleteWhere(address =>
            address.Kind == Kind && string.Equals(key, address.Key, StringComparison.Ordinal)
        );
    }

    private void CompleteWhere(Func<WatchAddress, bool> matches)
    {
        List<ChangeWaiter>? completed = null;
        lock (_watchGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            foreach (var (address, bucket) in _waiters)
            {
                if (bucket.Count != 0 && matches(address))
                {
                    (completed ??= []).AddRange(bucket);
                    bucket.Clear();
                }
            }

            if (completed is not null)
            {
                var empty = new List<WatchAddress>();
                foreach (var (address, bucket) in _waiters)
                {
                    if (bucket.Count == 0)
                    {
                        empty.Add(address);
                    }
                }

                foreach (var address in empty)
                {
                    _waiters.Remove(address);
                }
            }
        }

        if (completed is not null)
        {
            foreach (var waiter in completed)
            {
                waiter.TrySetResult();
            }
        }
    }

    private ValueTask<bool> EnsureSubscribedAsync(CancellationToken cancellationToken)
    {
        Task<bool>? subscribe;
        lock (_watchGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_subscribed)
            {
                return new ValueTask<bool>(true);
            }

            _subscribeTask ??= SubscribeCoreAsync();
            subscribe = _subscribeTask;
        }

        return AwaitSubscriptionAsync(subscribe, cancellationToken);

        async ValueTask<bool> AwaitSubscriptionAsync(Task<bool> task, CancellationToken token)
        {
            try
            {
                return await task.WaitAsync(token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                lock (_watchGate)
                {
                    if (ReferenceEquals(_subscribeTask, task))
                    {
                        _subscribeTask = null;
                    }
                }

                if (exception is OperationCanceledException && token.IsCancellationRequested)
                {
                    throw;
                }

                if (WebStorageJsModule.IsRuntimeGone(exception))
                {
                    return false;
                }

                throw;
            }
        }
    }

    private async Task<bool> SubscribeCoreAsync()
    {
        DotNetObjectReference<WebStorageChangeReceiver> receiver;
        lock (_watchGate)
        {
            _receiver ??= DotNetObjectReference.Create(new WebStorageChangeReceiver(this));
            receiver = _receiver;
        }

        try
        {
            await _jsModule.SubscribeAsync(_subscriptionId, receiver).ConfigureAwait(false);
        }
        catch (Exception exception) when (WebStorageJsModule.IsRuntimeGone(exception))
        {
            // A failed subscribe must not poison later waits: the runtime may simply not be
            // connected yet (prerender) or may reconnect later.
            lock (_watchGate)
            {
                _subscribeTask = null;
            }

            return false;
        }

        lock (_watchGate)
        {
            _subscribeTask = null;
            if (Volatile.Read(ref _disposed) == 0)
            {
                _subscribed = true;
                return true;
            }
        }

        // Disposed while the subscription was in flight: release the browser side
        // immediately so no DotNetObjectReference is retained.
        try
        {
            await _jsModule.UnsubscribeAsync(_subscriptionId).ConfigureAwait(false);
        }
        catch (Exception exception) when (WebStorageJsModule.IsRuntimeGone(exception))
        {
            // The runtime is going away; the browser-side listener dies with its realm.
        }

        return false;
    }

    private async ValueTask<string?> MutateAsync(
        string key,
        string encoded,
        RevisionCondition condition,
        CancellationToken cancellationToken
    )
    {
        string? status;
        try
        {
            status = await _jsModule
                .MutateAsync(
                    StorageName,
                    key,
                    encoded,
                    condition.Revision,
                    condition.IsMustNotExist,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (JSException exception)
        {
            // The isolated helper module (or its mutate entry) is unavailable, so the write
            // precondition cannot be enforced atomically. A disconnected or prerender
            // runtime surfaces InvalidOperationException instead and is reported as
            // unavailable by the caller.
            throw new WebStorageAtomicityNotSupportedException(
                "The browser-side Configlue storage helper is unavailable, so the write precondition cannot be enforced atomically.",
                exception
            );
        }

        return status;
    }

    private string ResolveKey(ConfiglueResourceContext context)
    {
        if (_keySelector is not null)
        {
            var selected = _keySelector(context);
            ArgumentException.ThrowIfNullOrWhiteSpace(selected);
            return selected;
        }

        return context.ResourceKey.IsDefault ? Key : Key + ":" + context.ResourceKey.Value;
    }

    private static bool StorageNameMatches(string? storageName, WebStorageKind kind)
    {
        if (storageName is null)
        {
            // Unknown area: wake conservatively; the waiter re-reads and converges.
            return true;
        }

        if (string.Equals(storageName, "localStorage", StringComparison.Ordinal))
        {
            return kind == WebStorageKind.Local;
        }

        if (string.Equals(storageName, "sessionStorage", StringComparison.Ordinal))
        {
            return kind == WebStorageKind.Session;
        }

        return true;
    }

    private static bool IsUnavailable(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is JSException or InvalidOperationException)
        {
            return true;
        }

        return exception is OperationCanceledException
            && !cancellationToken.IsCancellationRequested;
    }

    private static ResourceReadResult Decode(string raw)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<WebStorageEnvelope>(raw, EnvelopeOptions);
            if (envelope?.Content is not null)
            {
                return ResourceReadResult.Success(
                    Convert.FromBase64String(envelope.Content),
                    envelope.Revision
                );
            }
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            // Values written outside Configlue are treated as raw payload without an envelope.
        }

        var content = Encoding.UTF8.GetBytes(raw);
        return ResourceReadResult.Success(content, CreateRawRevision(raw));
    }

    private static string Encode(string revision, ReadOnlyMemory<byte> content)
    {
        var envelope = new WebStorageEnvelope
        {
            Revision = revision,
            Content = Convert.ToBase64String(content.Span),
        };
        return JsonSerializer.Serialize(envelope, EnvelopeOptions);
    }

    private static string CreateRawRevision(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

    private sealed class WebStorageEnvelope
    {
        public string? Revision { get; set; }

        public string? Content { get; set; }
    }

    private readonly record struct WatchAddress(WebStorageKind Kind, string Key);

    private sealed class ChangeWaiter
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task Task => _completion.Task;

        public void TrySetResult() => _completion.TrySetResult();

        public void TrySetCanceled() => _completion.TrySetCanceled();
    }
}
