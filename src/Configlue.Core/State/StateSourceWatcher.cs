namespace Configlue;

/// <summary>Watches the active source and higher-priority sources that may become active again.</summary>
public sealed class StateSourceWatcher<T> : IStateWatcher
{
    private readonly StateSourceResolver<T> _resolver;

    /// <summary>Creates a composite source watcher.</summary>
    public StateSourceWatcher(StateSourceResolver<T> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
    }

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        using var watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var watchers = new List<Task>();
        try
        {
            foreach (
                var target in _resolver
                    .GetSourcesForWatch(observedRevision)
                    .Where(static target => target.Source.Watcher is not null)
            )
            {
                watchers.Add(
                    target
                        .Source.Watcher!.WaitForChangeAsync(
                            target.ObservedRevision,
                            watchCancellation.Token
                        )
                        .AsTask()
                );
            }

            if (watchers.Count == 0)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return;
            }

            var finished = await Task.WhenAny(watchers).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
        }
        finally
        {
            await watchCancellation.CancelAsync().ConfigureAwait(false);
        }
    }
}
