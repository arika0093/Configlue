using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using Microsoft.UI.Xaml;

namespace Configlue.Hosting.Maui.Windows.Tests;

public sealed class MauiWindowsSmokeTests
{
    [Test]
    public async Task NativeMauiMainThread_UsesRegisteredWindowsUiWindow()
    {
        var dispatcher = new MauiConfiglueDispatcher();
        var ready = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var stopped = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var thread = new Thread(() =>
        {
            try
            {
                Application.Start(callback =>
                {
                    _ = new SmokeApplication();
                    var window = new Window();
                    // Match MAUI's native window registration without showing a window.
                    WindowStateManager.Default.OnPlatformWindowInitialized(window);
                    if (dispatcher.CheckAccess())
                        ready.TrySetResult(Environment.CurrentManagedThreadId);
                    else
                        ready.TrySetException(
                            new InvalidOperationException(
                                "MAUI did not recognize the native UI thread."
                            )
                        );
                });
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
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            var ownerThread = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            dispatcher.CheckAccess().ShouldBeFalse();
            var dispatched = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            dispatcher.Post(() => dispatched.TrySetResult(Environment.CurrentManagedThreadId));
            (await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(ownerThread);
            var invokedThread = 0;
            await dispatcher.InvokeAsync(() => invokedThread = Environment.CurrentManagedThreadId);
            invokedThread.ShouldBe(ownerThread);
        }
        finally
        {
            if (ready.Task.IsCompletedSuccessfully)
                dispatcher.Post(() => Application.Current.Exit());
            thread.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        }
        await stopped.Task;
    }

    [Test]
    public void NativeMauiFilesystem_ResolvesAppDataWithoutExtraApplicationId()
    {
        var builder = new ConfiglueBuilder().UseMaui();
        builder
            .ResolveStandardDirectory(ConfiglueStandardLocation.UserGlobal, "ignored-app-id")
            .ShouldBe(Path.GetFullPath(FileSystem.Current.AppDataDirectory));
        Path.IsPathRooted(builder.ResolveStandardDirectory(ConfiglueStandardLocation.BackupRoot))
            .ShouldBeTrue();
    }

    [Test]
    public async Task NativeMauiSecureStorage_RoundTripsSmallBinarySecret()
    {
        var storage = SecureStorage.Default;
        var key = "Configlue.Tests." + Guid.NewGuid().ToString("N");
        var resource = new SecureStorageResource(storage, key);
        var bytes = new byte[] { 0, 255, 128, 1 };
        try
        {
            (await resource.ReadAsync(default)).Status.ShouldBe(StateReadStatus.NotFound);
            var written = await resource.WriteAsync(
                default,
                new ResourceWriteRequest(bytes, RevisionCondition.MustNotExist)
            );
            var read = await resource.ReadAsync(default);
            read.Content.ToArray().ShouldBe(bytes);
            read.Revision.ShouldBe(written.Revision);
        }
        finally
        {
            storage.Remove(key);
        }
    }

    private sealed class SmokeApplication : Application { }
}
