using Configlue.Extensions.ComponentModel;
using Configlue.Hosting.Tests;
using Microsoft.UI.Dispatching;

namespace Configlue.Hosting.WinUI.Tests;

public sealed class WinUIDispatcherTests
{
    [Test]
    public async Task NativeQueue_DispatchesObservableUpdates_AndRejectsPostsAfterShutdown()
    {
        var controller = DispatcherQueueController.CreateOnDedicatedThread();
        var adapter = new WinUIConfiglueDispatcher(controller.DispatcherQueue);
        var state = new DispatcherTestState();
        using var reader = new ConfiglueStateReader<HostSettings>(state, adapter);
        var changed = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        try
        {
            adapter.CheckAccess().ShouldBeFalse();
            var owner = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            adapter.Post(() =>
            {
                if (!adapter.CheckAccess())
                    owner.TrySetException(new InvalidOperationException("Wrong queue thread."));
                else
                    owner.TrySetResult(Environment.CurrentManagedThreadId);
            });
            var ownerThread = await owner.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await reader.InitializeAsync();
            reader.PropertyChanged += (_, args) =>
            {
                if (
                    args.PropertyName == nameof(reader.Value)
                    && ((HostSettings.Observable)reader.Value!).Counter == 2
                )
                    changed.TrySetResult(Environment.CurrentManagedThreadId);
            };
            await Task.Run(() => state.Push(2));
            (await changed.Task.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(ownerThread);
            reader.LoadFailure.ShouldBeNull();
        }
        finally
        {
            reader.Dispose();
            await controller.ShutdownQueueAsync();
        }
        Should.Throw<InvalidOperationException>(() => adapter.Post(static () => { }));
    }
}
