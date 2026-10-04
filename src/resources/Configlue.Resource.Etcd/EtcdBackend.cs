namespace Configlue.Resource.Etcd;

using System.Runtime.ExceptionServices;

/// <summary>Coordinates etcd range reads, compare-and-swap transactions, and prefix watches.</summary>
internal sealed class EtcdStateBackend : IDisposable
{
    private readonly IEtcdKvClient _kv;
    private readonly IEtcdWatcherClient _watcher;
    private readonly EtcdResourceOptions _options;
    private int _disposed;

    public EtcdStateBackend(
        IEtcdKvClient kv,
        IEtcdWatcherClient watcher,
        EtcdResourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(kv);
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(options);
        _kv = kv;
        _watcher = watcher;
        _options = options;
    }

    public IEtcdKvClient Kv => _kv;

    public async ValueTask<EtcdRangeResponse> GetPrefixAsync(
        string prefix,
        long? revision,
        CancellationToken cancellationToken
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        return await _kv.GetPrefixAsync(prefix, revision, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<EtcdTxnResponse> TransactAsync(
        IReadOnlyList<EtcdCompare> compares,
        IReadOnlyList<EtcdWrite> writes,
        CancellationToken cancellationToken
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(compares);
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
        {
            throw new ArgumentException(
                "An etcd transaction requires at least one write.",
                nameof(writes)
            );
        }

        return await _kv.TransactAsync(compares, writes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for an invalidation signal on <paramref name="prefix"/>. The watcher is
    /// invalidation-only: callers converge by re-reading current state after this returns.
    /// Duplicate and progress notifications are tolerated without waking the caller.
    /// </summary>
    public async ValueTask WaitForChangeAsync(
        string prefix,
        string? observedRevision,
        Func<CancellationToken, ValueTask<string?>> readCurrentRevision,
        CancellationToken cancellationToken
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(readCurrentRevision);

        var current = await readCurrentRevision(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current, observedRevision, StringComparison.Ordinal))
        {
            return;
        }

        var resumeRevision = ParseHeaderRevision(observedRevision);
        if (resumeRevision is null)
        {
            resumeRevision = ParseHeaderRevision(current);
        }

        var attempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            try
            {
                await _watcher
                    .WatchPrefixAsync(
                        prefix,
                        resumeRevision,
                        (response, _) =>
                        {
                            if (response.Events.Count == 0 || response.IsProgressNotification)
                            {
                                return new ValueTask<bool>(false);
                            }

                            return new ValueTask<bool>(true);
                        },
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return;
            }
            catch (EtcdCompactedException)
            {
                _ = await readCurrentRevision(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (EtcdTransientException) when (!cancellationToken.IsCancellationRequested)
            {
                attempts++;
                if (_options.MaxReconnectAttempts > 0 && attempts > _options.MaxReconnectAttempts)
                {
                    throw;
                }

                var latest = await readCurrentRevision(cancellationToken).ConfigureAwait(false);
                if (!string.Equals(latest, observedRevision, StringComparison.Ordinal))
                {
                    return;
                }

                resumeRevision = ParseHeaderRevision(latest) ?? resumeRevision;
                await DelayReconnectAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }

    private async ValueTask DelayReconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_options.ReconnectDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(exception.Message, exception, cancellationToken);
        }
    }

    internal static long? ParseHeaderRevision(string? revision)
    {
        if (revision is not null && EtcdRevisionCodec.TryDecode(revision, out var header, out _))
        {
            return header;
        }

        if (
            revision is not null
            && long.TryParse(
                revision,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var bare
            )
            && bare > 0
        )
        {
            return bare;
        }

        return null;
    }
}

/// <summary>Stops owned watches without disposing the caller-owned etcd client.</summary>
internal sealed class EtcdWatchShutdown
{
    private readonly TaskCompletionSource<bool> _shutdown = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public void Signal() => _shutdown.TrySetResult(true);

    public async ValueTask WaitAsync(
        Func<CancellationToken, ValueTask> waitForChange,
        CancellationToken cancellationToken
    )
    {
        using var watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var watch = waitForChange(watchCancellation.Token).AsTask();
        var completed = await Task.WhenAny(watch, _shutdown.Task).ConfigureAwait(false);
        var stopping = completed == _shutdown.Task;
        ExceptionDispatchInfo? cancellationFailure = null;
        try
        {
            if (stopping)
            {
#if NETSTANDARD
                watchCancellation.Cancel();
#else
                await watchCancellation.CancelAsync().ConfigureAwait(false);
#endif
            }
        }
        catch (Exception exception)
        {
            cancellationFailure = ExceptionDispatchInfo.Capture(exception);
        }

        try
        {
            await watch.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (stopping && !cancellationToken.IsCancellationRequested)
        {
            // Resource disposal historically wakes watchers successfully. Caller
            // cancellation still propagates with its original meaning.
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(exception.Message, exception, cancellationToken);
        }

        cancellationFailure?.Throw();
    }
}
