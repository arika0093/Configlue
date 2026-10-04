namespace Configlue.Tests;

public sealed class RuntimeLifetimeAsyncLeaseTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ShutdownDrainsAnOperationThatResumesNormallyOrWithAnException(bool fail)
    {
        var lifetime = new RuntimeLifetime();
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = WorkAsync();
        lifetime.TryBeginShutdown(out var drained).ShouldBeTrue();
        drained.IsCompleted.ShouldBeFalse();
        Should.Throw<ObjectDisposedException>(() => lifetime.EnterOperation());
        resume.SetResult();
        if (fail)
            await Should.ThrowAsync<InvalidOperationException>(() => work);
        else
            await work;
        drained.Status.ShouldBe(TaskStatus.RanToCompletion);

        async Task WorkAsync()
        {
            using var lease = lifetime.EnterOperation();
            await resume.Task;
            if (fail)
                throw new InvalidOperationException("Operation failure.");
        }
    }
}
