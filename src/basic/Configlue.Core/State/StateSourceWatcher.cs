using Configlue.Resources;
using Configlue.Sources;

namespace Configlue.State;

/// <summary>Watches the active source and higher-priority sources that may become active again.</summary>
/// <remarks>
/// Internal composition implementation (see issue #225). The supported extension surface is
/// <c>ISourceWatcher</c>; watch composition is exposed through source registration
/// (<c>StateSourceSetBuilder{T}</c>, <c>StateSource{T}</c>) rather than by constructing this
/// type directly.
/// </remarks>
internal sealed class StateSourceWatcher<T> : ISourceWatcher
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
        using var watchTargets = subject is null
            ? _resolver.GetSourcesForWatch(observedRevision)
            : _resolver.GetSourcesForWatch(subject, context.Route, observedRevision);
        var targets = watchTargets.Targets;

        var watchableCount = 0;
        StateSourceWatchTarget<T> singleTarget = default;
        for (var index = 0; index < targets.Count; index++)
        {
            if (targets[index].Source.Watcher is not null)
            {
                watchableCount++;
                if (watchableCount == 1)
                {
                    singleTarget = targets[index];
                }
            }
        }

        if (watchableCount == 0)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (watchableCount == 1)
        {
            await singleTarget
                .Source.WaitForChangeAsync(
                    singleTarget.EffectiveContext,
                    singleTarget.ObservedRevision,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return;
        }

        await WaitManyAsync(watchTargets, watchableCount, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WaitManyAsync(
        StateSourceWatchTargets<T> watchTargets,
        int watchableCount,
        CancellationToken cancellationToken
    )
    {
        using var watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var watchers = new Task[watchableCount];
        var position = 0;
        var targets = watchTargets.Targets;
        try
        {
            for (var index = 0; index < targets.Count; index++)
            {
                var target = targets[index];
                if (target.Source.Watcher is null)
                {
                    continue;
                }

                var task = target
                    .Source.WaitForChangeAsync(
                        target.EffectiveContext,
                        target.ObservedRevision,
                        watchCancellation.Token
                    )
                    .AsTask();
                watchers[position++] = task;
            }
        }
        catch
        {
            // A synchronously-throwing watcher must not strand already-started siblings.
            if (position > 0)
            {
                if (!watchCancellation.IsCancellationRequested)
                {
                    await watchCancellation.CancelAsync().ConfigureAwait(false);
                }

                await AwaitWatchersAsync(
                        new ArraySegment<Task>(watchers, 0, position),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            throw;
        }

        try
        {
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
