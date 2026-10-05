using Configlue.Internal;

namespace Configlue.Tests;

/// <summary>
/// Covers the shared generic polling-watch primitive once so provider suites only
/// need revision-extraction and status-mapping tests.
/// </summary>
public sealed class PollingWatchTests
{
    [Test]
    public async Task ReturnsImmediatelyWhenRevisionAlreadyDiffers()
    {
        var reads = 0;

        await PollingWatch.WaitForRevisionChangeAsync(
            _ =>
            {
                reads++;
                return new ValueTask<string?>("rev-2");
            },
            "rev-1",
            TimeSpan.FromMilliseconds(10)
        );

        reads.ShouldBe(1);
    }

    [Test]
    public async Task PollsUntilRevisionChanges()
    {
        var reads = 0;

        await PollingWatch.WaitForRevisionChangeAsync(
            _ =>
            {
                reads++;
                return new ValueTask<string?>(reads < 3 ? "rev-1" : "rev-2");
            },
            "rev-1",
            TimeSpan.FromMilliseconds(10)
        );

        reads.ShouldBe(3);
    }

    [Test]
    public async Task TransientFailuresAreTreatedAsUnchanged()
    {
        var reads = 0;
        var task = PollingWatch
            .WaitForRevisionChangeAsync(
                _ =>
                {
                    reads++;
                    if (reads == 1)
                    {
                        throw new InvalidOperationException("transient");
                    }

                    return new ValueTask<string?>("rev-2");
                },
                "rev-1",
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None,
                static exception => exception is InvalidOperationException
            )
            .AsTask();

        await task.WaitAsync(TimeSpan.FromSeconds(5));
        reads.ShouldBe(2);
    }

    [Test]
    public async Task NonTransientFailuresPropagate()
    {
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await PollingWatch.WaitForRevisionChangeAsync(
                _ => throw new InvalidOperationException("fatal"),
                "rev-1",
                TimeSpan.FromMilliseconds(10)
            )
        );
    }

    [Test]
    public async Task HonorsCancellation()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await PollingWatch.WaitForRevisionChangeAsync(
                _ => new ValueTask<string?>("rev-1"),
                "rev-1",
                TimeSpan.FromMilliseconds(10),
                canceled.Token
            )
        );

        using var polling = new CancellationTokenSource();
        polling.CancelAfter(50);
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await PollingWatch.WaitForRevisionChangeAsync(
                _ => new ValueTask<string?>("rev-1"),
                "rev-1",
                TimeSpan.FromMilliseconds(10),
                polling.Token
            )
        );
    }

    [Test]
    public async Task RejectsNonPositiveIntervals()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await PollingWatch.WaitForRevisionChangeAsync(
                _ => new ValueTask<string?>("rev-1"),
                "rev-1",
                TimeSpan.Zero
            )
        );
    }

    [Test]
    public async Task DisposalWakesWaitersSuccessfully()
    {
        var shutdown = new WatchShutdown();
        using var caller = new CancellationTokenSource();
        var wait = shutdown
            .WaitAsync(
                watchCancellationToken =>
                    PollingWatch.WaitForRevisionChangeAsync(
                        _ => new ValueTask<string?>("rev-1"),
                        "rev-1",
                        TimeSpan.FromMilliseconds(10),
                        watchCancellationToken
                    ),
                caller.Token
            )
            .AsTask();

        await Task.Delay(50);
        wait.IsCompleted.ShouldBeFalse();
        shutdown.Signal();

        // Common disposal semantics for every generic polling watch: disposal wakes
        // the waiter successfully while caller cancellation still throws.
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
