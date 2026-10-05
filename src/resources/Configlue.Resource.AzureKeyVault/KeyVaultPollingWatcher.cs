using Configlue.Internal;

namespace Configlue.Resource.AzureKeyVault;

/// <summary>Polls a revision provider until it differs from the observed revision.</summary>
/// <remarks>
/// Thin adapter over the shared <see cref="PollingWatch"/> primitive so single-secret
/// Key Vault documents reuse the common cancellation/disposal/transient semantics.
/// </remarks>
internal sealed class KeyVaultPollingWatcher : ISourceWatcher, IDisposable
{
    private readonly Func<CancellationToken, ValueTask<string?>> _getRevisionAsync;
    private readonly TimeSpan _pollInterval;
    private readonly WatchShutdown _watchShutdown = new();
    private int _disposed;

    internal KeyVaultPollingWatcher(
        Func<CancellationToken, ValueTask<string?>> getRevisionAsync,
        TimeSpan pollInterval
    )
    {
        ArgumentNullException.ThrowIfNull(getRevisionAsync);
        PollingWatch.ValidateInterval(pollInterval, nameof(pollInterval));

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

        await _watchShutdown
            .WaitAsync(
                watchCancellationToken =>
                    PollingWatch.WaitForRevisionChangeAsync(
                        _getRevisionAsync,
                        observedRevision,
                        _pollInterval,
                        watchCancellationToken,
                        static exception => exception is KeyVaultSecretUnavailableException
                    ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _watchShutdown.Signal();
    }

    public override string ToString() => "KeyVaultPollingWatcher(watcher=REDACTED)";
}
