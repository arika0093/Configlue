using Configlue.Resources;

namespace Configlue.State;

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
    public ValueTask WaitForChangeAsync(
        string? observedRevision,
        CancellationToken cancellationToken = default
    ) => WaitCoreAsync(null, observedRevision, cancellationToken);

    /// <summary>Waits for source changes affecting one subject.</summary>
    public ValueTask WaitForChangeAsync(
        IConfiglueSubject subject,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(subject);
        return WaitCoreAsync(subject, observedRevision, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    ) => WaitForChangeAsync(context.Subject, observedRevision, cancellationToken);

    private async ValueTask WaitCoreAsync(
        IConfiglueSubject? subject,
        string? observedRevision,
        CancellationToken cancellationToken
    )
    {
        using var watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var watchers = new List<Task>();
        try
        {
            foreach (
                var target in (
                    subject is null
                        ? _resolver.GetSourcesForWatch(observedRevision)
                        : _resolver.GetSourcesForWatch(subject, observedRevision)
                ).Where(static target => target.Source.Watcher is not null)
            )
            {
                watchers.Add(
                    (
                        subject is null
                            ? target.Source.Watcher!.WaitForChangeAsync(
                                target.ObservedRevision,
                                watchCancellation.Token
                            )
                            : target.Source.WaitForChangeAsync(
                                subject,
                                target.ObservedRevision,
                                watchCancellation.Token
                            )
                    ).AsTask()
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
