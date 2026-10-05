using Configlue.Sources;
using Configlue.State;

namespace Configlue.Tests;

public sealed class StateSourceWatcherTests
{
    [Test]
    public async Task WaitForChangeAsync_DrainsCanceledLosersBeforeReturning()
    {
        var winner = new ControlledWatcher();
        var loser = new ControlledWatcher(delayCancellationCleanup: true);
        var watcher = CreateWatcher(winner, loser);

        var waiting = watcher.WaitForChangeAsync(ConfiglueResourceContext.Default, null).AsTask();
        await Task.WhenAll(winner.Started.Task, loser.Started.Task);

        winner.Release();
        await loser.CleanupStarted.Task;

        waiting.IsCompleted.ShouldBeFalse();
        loser.ReleaseCleanup();
        await waiting;
    }

    [Test]
    public async Task WaitForChangeAsync_PreservesCallerCancellation()
    {
        var first = new BlockingWatcher();
        var second = new BlockingWatcher();
        var watcher = CreateWatcher(first, second);
        using var cancellation = new CancellationTokenSource();

        var waiting = watcher
            .WaitForChangeAsync(ConfiglueResourceContext.Default, null, cancellation.Token)
            .AsTask();
        await Task.WhenAll(first.Started.Task, second.Started.Task);
        cancellation.Cancel();

        var exception = await Should.ThrowAsync<OperationCanceledException>(async () => await waiting);
        exception.CancellationToken.ShouldBe(cancellation.Token);
    }

    [Test]
    public async Task WaitForChangeAsync_ObservesFailureFromCanceledLoser()
    {
        var winner = new ControlledWatcher();
        var loser = new ControlledWatcher(delayCancellationCleanup: true, failAfterCancellation: true);
        var watcher = CreateWatcher(winner, loser);

        var waiting = watcher.WaitForChangeAsync(ConfiglueResourceContext.Default, null).AsTask();
        await Task.WhenAll(winner.Started.Task, loser.Started.Task);
        winner.Release();
        await loser.CleanupStarted.Task;
        waiting.IsCompleted.ShouldBeFalse();
        loser.ReleaseCleanup();

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () => await waiting);
        exception.Message.ShouldBe("Loser watcher cleanup failed.");
    }

    [Test]
    public async Task WaitForChangeAsync_SnapshotSurvivesIdleEvictionWhileLosersDrain()
    {
        var higherPriorityWatcher = new ControlledWatcher(
            delayCancellationCleanup: true
        );
        var activeWatcher = new ControlledWatcher();
        var higherPrioritySource = CreateSource(
            "higher",
            priority: 10,
            new FixedReader(StateReadStatus.NotFound),
            higherPriorityWatcher,
            StateFallbackCondition.NotFound
        );
        var activeSource = CreateSource(
            "active",
            priority: 0,
            new FixedReader(StateReadStatus.Success),
            activeWatcher,
            StateFallbackCondition.None
        );
        var resolver = new StateSourceResolver<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([higherPrioritySource, activeSource]),
            subjectResolutionIdleTimeout: TimeSpan.FromMilliseconds(1)
        );
        var subject = new WatchSubject("subject-one");
        var context = higherPrioritySource.GetResourceContext(subject);
        (await resolver.ReadAsync(context)).Status.ShouldBe(StateReadStatus.Success);
        resolver.SubjectResolutionCount.ShouldBe(1);

        var watcher = new StateSourceWatcher<AppSettings.Fragment>(resolver);
        var waiting = watcher.WaitForChangeAsync(context, "active-revision").AsTask();
        await Task.WhenAll(higherPriorityWatcher.Started.Task, activeWatcher.Started.Task);
        activeWatcher.Release();
        await higherPriorityWatcher.CleanupStarted.Task;

        await Task.Delay(TimeSpan.FromMilliseconds(20));
        (await resolver.ReadAsync(higherPrioritySource.GetResourceContext(new WatchSubject("subject-two"))))
            .Status.ShouldBe(StateReadStatus.Success);
        // Watches hold an immutable snapshot, not residency (issue #271): the idle entry for the
        // watched subject is evicted while its losers still drain, and the watch still completes.
        resolver.SubjectResolutionCount.ShouldBe(1);

        higherPriorityWatcher.ReleaseCleanup();
        await waiting;
        await Task.Delay(TimeSpan.FromMilliseconds(20));
        (await resolver.ReadAsync(higherPrioritySource.GetResourceContext(new WatchSubject("subject-three"))))
            .Status.ShouldBe(StateReadStatus.Success);
        resolver.SubjectResolutionCount.ShouldBe(1);
    }

    private static StateSourceWatcher<AppSettings.Fragment> CreateWatcher(
        ISourceWatcher first,
        ISourceWatcher second
    ) =>
        new(
            new StateSourceResolver<AppSettings.Fragment>(
                new StateSourceSet<AppSettings.Fragment>(
                [
                    CreateSource("first", 1, new FixedReader(StateReadStatus.Success), first),
                    CreateSource("second", 0, new FixedReader(StateReadStatus.Success), second),
                ]
                )
            )
        );

    private static StateSource<AppSettings.Fragment> CreateSource(
        string id,
        int priority,
        ISourceReader<AppSettings.Fragment> reader,
        ISourceWatcher watcher,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.None
    ) =>
        new StateSource<AppSettings.Fragment>(id, reader, new StateSourceOptions<AppSettings.Fragment> { Priority = priority, FallbackCondition = fallbackCondition, Watcher = watcher });

    private sealed class FixedReader(StateReadStatus status) : ISourceReader<AppSettings.Fragment>
    {
        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTaskCompat.FromResult(
                status == StateReadStatus.Success
                    ? StateReadResult<AppSettings.Fragment>.Success(
                        new AppSettings.Fragment(),
                        "active-revision"
                    )
                    : StateReadResult<AppSettings.Fragment>.NotFound("missing-revision")
            );
        }
    }

    private sealed class BlockingWatcher : ISourceWatcher
    {
        public TaskCompletionSource Started { get; } = NewSignal();

        public async ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        )
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ControlledWatcher(
        bool delayCancellationCleanup = false,
        bool failAfterCancellation = false
    ) : ISourceWatcher
    {
        private readonly TaskCompletionSource _releaseWinner = NewSignal();
        private readonly TaskCompletionSource _releaseCleanup = NewSignal();

        public TaskCompletionSource Started { get; } = NewSignal();

        public TaskCompletionSource CleanupStarted { get; } = NewSignal();

        public void Release() => _releaseWinner.TrySetResult();

        public void ReleaseCleanup() => _releaseCleanup.TrySetResult();

        public async ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        )
        {
            Started.TrySetResult();
            if (!delayCancellationCleanup && !failAfterCancellation)
            {
                await _releaseWinner.Task;
                return;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!delayCancellationCleanup)
                {
                    throw;
                }

                CleanupStarted.TrySetResult();
                await _releaseCleanup.Task;
                if (failAfterCancellation)
                {
                    throw new InvalidOperationException("Loser watcher cleanup failed.");
                }

                throw;
            }
        }
    }

    private sealed record WatchSubject(string Name) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From(Name);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
