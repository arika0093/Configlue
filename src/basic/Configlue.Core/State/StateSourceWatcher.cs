using Configlue.Resources;
using Configlue.Sources;

namespace Configlue.State;

/// <summary>Watches the active source and higher-priority sources that may become active again.</summary>
public sealed class StateSourceWatcher<T> : ISourceWatcher
{
    private readonly StateSourceResolver<T> _resolver;

    /// <summary>Creates a composite source watcher.</summary>
    public StateSourceWatcher(StateSourceResolver<T> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
    }

    /// <inheritdoc />
    public ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        context = ConfiglueResourceContext.Normalize(context);
        var subject = context.IsDefault ? null : context.Subject;
        return WaitCoreAsync(subject, context, observedRevision, cancellationToken);
    }

    private async ValueTask WaitCoreAsync(
        IConfiglueSubject? subject,
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken
    )
    {
        using var watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        using var watchTargets = subject is null
            ? _resolver.GetSourcesForWatch(observedRevision)
            : _resolver.GetSourcesForWatch(subject, context.Route, observedRevision);
        var watchers = new List<Task>();
        try
        {
            foreach (
                var target in watchTargets.Targets.Where(static target =>
                    target.Source.Watcher is not null
                )
            )
            {
                var sourceContext = subject is null
                    ? context
                    : target.Source.GetResourceContext(subject);
                watchers.Add(
                    target
                        .Source.WaitForChangeAsync(
                            sourceContext,
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
            if (!watchCancellation.IsCancellationRequested)
            {
                await watchCancellation.CancelAsync().ConfigureAwait(false);
            }

            await AwaitWatchersAsync(watchers, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task AwaitWatchersAsync(
        IReadOnlyList<Task> watchers,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await Task.WhenAll(watchers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
