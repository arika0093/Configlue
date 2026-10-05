namespace Configlue.Internal;

/// <summary>
/// Shared primitive for generic periodic revision polling watches.
/// </summary>
/// <remarks>
/// <para>
/// Covers the <c>read revision -&gt; delay -&gt; complete when changed</c> shape used by
/// remote providers without a native push or protocol-defined polling mechanism
/// (Azure Blob ETags, Google Secret Manager versions, AWS Secrets Manager stages,
/// SSM Parameter Store revisions, Azure App Configuration selections, and similar
/// metadata-only polls).
/// </para>
/// <para>
/// Native blocking watches (Consul) and protocol-defined session polling with
/// server-provided intervals (AWS AppConfig Data API) intentionally stay
/// provider-specific and must not be forced through this helper.
/// </para>
/// <para>
/// Cancellation and disposal are unified through the caller's token: providers wrap
/// this loop with <see cref="WatchShutdown"/> so disposal cancels the linked token
/// and wakes the waiter successfully, while caller cancellation propagates as
/// <see cref="OperationCanceledException"/>.
/// </para>
/// </remarks>
internal static class PollingWatch
{
    /// <summary>Validates a polling interval shared by all generic polling watches.</summary>
    public static void ValidateInterval(TimeSpan pollInterval, string paramName = "pollInterval")
    {
        if (pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                "The polling interval must be greater than zero."
            );
        }
    }

    /// <summary>
    /// Polls <paramref name="readRevisionAsync"/> until it differs from
    /// <paramref name="observedRevision"/>.
    /// </summary>
    /// <param name="readRevisionAsync">
    /// Lightweight revision read without payload download. Providers map domain-specific
    /// absence (missing blob, deleted secret, unknown version) to a revision value
    /// (usually <c>null</c>) so comparison stays a plain ordinal string comparison.
    /// </param>
    /// <param name="observedRevision">The revision the caller has already observed.</param>
    /// <param name="pollInterval">The delay between revision reads. Must be positive.</param>
    /// <param name="cancellationToken">
    /// Caller cancellation, already linked with disposal via <see cref="WatchShutdown"/>.
    /// </param>
    /// <param name="isTransient">
    /// Optional transient classifier. When it returns <c>true</c> for a read failure,
    /// the poll is treated as "unchanged for this tick" and retried after the next
    /// delay instead of failing the watch.
    /// </param>
    public static async ValueTask WaitForRevisionChangeAsync(
        Func<CancellationToken, ValueTask<string?>> readRevisionAsync,
        string? observedRevision,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default,
        Func<Exception, bool>? isTransient = null
    )
    {
        ArgumentNullException.ThrowIfNull(readRevisionAsync);
        ValidateInterval(pollInterval);
        cancellationToken.ThrowIfCancellationRequested();

        var current = await ReadAsync(
                readRevisionAsync,
                observedRevision,
                cancellationToken,
                isTransient
            )
            .ConfigureAwait(false);
        if (!string.Equals(current, observedRevision, StringComparison.Ordinal))
        {
            return;
        }

        while (true)
        {
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            current = await ReadAsync(
                    readRevisionAsync,
                    observedRevision,
                    cancellationToken,
                    isTransient
                )
                .ConfigureAwait(false);
            if (!string.Equals(current, observedRevision, StringComparison.Ordinal))
            {
                return;
            }
        }
    }

    private static async ValueTask<string?> ReadAsync(
        Func<CancellationToken, ValueTask<string?>> readRevisionAsync,
        string? observedRevision,
        CancellationToken cancellationToken,
        Func<Exception, bool>? isTransient
    )
    {
        try
        {
            return await readRevisionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (isTransient?.Invoke(exception) is true
                && !cancellationToken.IsCancellationRequested
            )
        {
            // Transient refresh failures retain the last good state: the watcher keeps
            // waiting instead of signaling an empty payload or failing the watch.
            return observedRevision;
        }
    }
}
