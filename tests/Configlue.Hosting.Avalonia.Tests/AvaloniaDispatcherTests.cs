using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Configlue.Extensions.ComponentModel;
using Configlue.Hosting.Tests;

namespace Configlue.Hosting.Avalonia.Tests;

public sealed class AvaloniaDispatcherTests
{
    [Test]
    public async Task NativeUiDispatcher_MarshalsBackgroundNotificationsToObservableModel()
    {
        var ready = new TaskCompletionSource<(IConfiglueDispatcher Dispatcher, int ThreadId)>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var changed = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var stopped = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var state = new DispatcherTestState();
        using var cancellation = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder
                    .Configure<HeadlessApplication>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                    .SetupWithoutStarting();
                var adapter = new AvaloniaConfiglueDispatcher();
                adapter.CheckAccess().ShouldBeTrue();
                using var reader = new ConfiglueStateReader<HostSettings>(state, adapter);
                reader.InitializeAsync().GetAwaiter().GetResult();
                ((HostSettings.Observable)reader.Value!).Counter.ShouldBe(1);
                reader.PropertyChanged += (_, args) =>
                {
                    if (
                        args.PropertyName == nameof(reader.Value)
                        && ((HostSettings.Observable)reader.Value!).Counter == 2
                    )
                        changed.TrySetResult(Environment.CurrentManagedThreadId);
                };
                ready.SetResult((adapter, Environment.CurrentManagedThreadId));
                Dispatcher.UIThread.MainLoop(cancellation.Token);
                stopped.TrySetResult(true);
            }
            catch (Exception exception)
            {
                ready.TrySetException(exception);
                stopped.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        try
        {
            var host = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            host.Dispatcher.CheckAccess().ShouldBeFalse();
            await Task.Run(() => state.Push(2));
            (await changed.Task.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(host.ThreadId);
        }
        finally
        {
            cancellation.Cancel();
            thread.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        }
        await stopped.Task;
    }

    private sealed class HeadlessApplication : Application { }
}
