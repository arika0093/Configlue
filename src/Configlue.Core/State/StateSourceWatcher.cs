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
        CancellationToken cancellationToken = default)
    {
        using var watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watchers = _resolver.GetSourcesForWatch()
            .Where(static source => source.Watcher is not null)
            .Select(source => source.Watcher!.WaitForChangeAsync(observedRevision, watchCancellation.Token).AsTask())
            .ToArray();

        if (watchers.Length == 0)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return;
        }

        var finished = await Task.WhenAny(watchers).ConfigureAwait(false);
        watchCancellation.Cancel();
        await finished.ConfigureAwait(false);
    }
}
