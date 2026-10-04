namespace Configlue.Resource.AzureKeyVault;

/// <summary>Polls a revision provider until it differs from the observed revision.</summary>
internal sealed class KeyVaultPollingWatcher : ISourceWatcher, IDisposable
{
    private readonly Func<CancellationToken, ValueTask<string?>> _getRevisionAsync;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _disposeSignal = new();
    private int _disposed;

    internal KeyVaultPollingWatcher(
        Func<CancellationToken, ValueTask<string?>> getRevisionAsync,
        TimeSpan pollInterval
    )
    {
        ArgumentNullException.ThrowIfNull(getRevisionAsync);
        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollInterval),
                "The polling interval must be greater than zero."
            );
        }

        _getRevisionAsync = getRevisionAsync;
        _pollInterval = pollInterval;
    }

    public async ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        _ = context;
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        string? current;
        try
        {
            current = await _getRevisionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (KeyVaultSecretUnavailableException)
        {
            current = observedRevision;
        }

        if (!string.Equals(current, observedRevision, StringComparison.Ordinal))
        {
            return;
        }

        while (true)
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _disposeSignal.Token
                );
                await Task.Delay(_pollInterval, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (_disposeSignal.IsCancellationRequested)
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                    return;
                }

                throw;
            }

            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                current = await _getRevisionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (KeyVaultSecretUnavailableException)
            {
                continue;
            }

            if (!string.Equals(current, observedRevision, StringComparison.Ordinal))
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposeSignal.Cancel();
        _disposeSignal.Dispose();
    }

    public override string ToString() => "KeyVaultPollingWatcher(watcher=REDACTED)";
}
