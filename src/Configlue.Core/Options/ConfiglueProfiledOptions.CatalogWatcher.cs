using System.Diagnostics;

namespace Configlue;

public sealed partial class ConfiglueProfiledOptions<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private void StartCatalogWatcher()
    {
        lock (_subscriptionGate)
        {
            if (
                _catalogSource.Watcher is null
                || _catalogWatchTask is not null
                || Volatile.Read(ref _disposed) != 0
            )
            {
                return;
            }

            _catalogWatchTask = WatchCatalogAsync(_watcherCancellation.Token);
        }
    }

    private async Task WatchCatalogAsync(CancellationToken cancellationToken)
    {
        var refreshPending = false;
        while (true)
        {
            if (!refreshPending)
            {
                try
                {
                    await _catalogSource
                        .Watcher!.WaitForChangeAsync(_catalogRevision, cancellationToken)
                        .ConfigureAwait(false);
                    refreshPending = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }
                catch (Exception exception)
                {
                    Trace.TraceError("Configlue profile catalog watcher failed: {0}", exception);
                    var shouldRetry = await DelayCatalogWatcherRetryAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (!shouldRetry)
                    {
                        return;
                    }

                    refreshPending = true;
                    continue;
                }
            }

            try
            {
                await RefreshCatalogFromWatcherAsync(cancellationToken).ConfigureAwait(false);
                refreshPending = false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            catch (Exception exception)
            {
                Trace.TraceError("Configlue profile catalog refresh failed: {0}", exception);
                var shouldRetry = await DelayCatalogWatcherRetryAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!shouldRetry)
                {
                    return;
                }
            }
        }
    }

    private static async Task<bool> DelayCatalogWatcherRetryAsync(
        CancellationToken cancellationToken
    )
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task RefreshCatalogFromWatcherAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IConfiglueOptionsRegistryNotificationDeferral<TModel>? notificationScope = null;
        try
        {
            notificationScope = DeferRegistryNotifications();
            _initialized = false;
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            try
            {
                notificationScope?.Dispose();
            }
            finally
            {
                _ = Task.Run(DrainPendingActiveProfileNotifications, CancellationToken.None);
            }
        }
    }

    private async Task DisposeCoreAsync(
        Task? catalogWatchTask,
        ActiveProfileValueSubscription[] subscriptions
    )
    {
        // Leave the owner's subscription lock before cancelling sources or awaiting work.
        await Task.Yield();
        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }
        await Task.WhenAll(
                subscriptions.Select(static subscription => subscription.WaitForCompletionAsync())
            )
            .ConfigureAwait(false);
        if (catalogWatchTask is not null)
        {
            await catalogWatchTask.ConfigureAwait(false);
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Release();
        _watcherCancellation.Dispose();
    }
}
