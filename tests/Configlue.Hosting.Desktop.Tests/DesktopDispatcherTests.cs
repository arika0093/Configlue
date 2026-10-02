using System.Windows.Threading;
using Configlue.Extensions.ComponentModel;
using Configlue.Hosting.WinForms;
using Configlue.Hosting.Wpf;
using Forms = System.Windows.Forms;

namespace Configlue.Hosting.Desktop.Tests;

public sealed class DesktopDispatcherTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task BackgroundStateNotification_UpdatesObservableValueOnUiThread(bool wpf)
    {
        using var host = await UiThread.CreateAsync(wpf);
        var state = new NotificationState();
        using var reader = new ConfiglueStateReader<DesktopSettings>(state, host.Dispatcher);
        await reader.InitializeAsync();
        await host.InvokeAsync(() =>
        {
            ((DesktopSettings.Observable)reader.Value!).Counter.ShouldBe(1);
            return true;
        });
        var changed = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        reader.PropertyChanged += (_, args) =>
        {
            if (
                args.PropertyName == nameof(reader.Value)
                && ((DesktopSettings.Observable)reader.Value!).Counter == 2
            )
                changed.TrySetResult(Environment.CurrentManagedThreadId);
        };
        await Task.Run(() => state.Push(new DesktopSettings { Counter = 2 }));
        (await changed.Task.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(host.ThreadId);
        reader.LoadFailure.ShouldBeNull();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Post_FromBackgroundThread_RunsOnExplicitUiThread(bool wpf)
    {
        using var host = await UiThread.CreateAsync(wpf);
        host.Dispatcher.CheckAccess().ShouldBeFalse();
        var observed = await host.InvokeAsync(() =>
        {
            host.Dispatcher.CheckAccess().ShouldBeTrue();
            return Environment.CurrentManagedThreadId;
        });
        observed.ShouldBe(host.ThreadId);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task InvokeAsync_FromBackgroundThread_RunsOnExplicitUiThread(bool wpf)
    {
        using var host = await UiThread.CreateAsync(wpf);
        host.Dispatcher.CheckAccess().ShouldBeFalse();
        var observed = 0;

        await host.Dispatcher.InvokeAsync(() => observed = Environment.CurrentManagedThreadId);

        observed.ShouldBe(host.ThreadId);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task InvokeAsync_AfterShutdown_FailsExplicitly(bool wpf)
    {
        var host = await UiThread.CreateAsync(wpf);
        host.Dispose();
        host.Dispatcher.CheckAccess().ShouldBeFalse();
        if (wpf)
            await Should.ThrowAsync<InvalidOperationException>(async () =>
                await host.Dispatcher.InvokeAsync(static () => { })
            );
        else
            await Should.ThrowAsync<ObjectDisposedException>(async () =>
                await host.Dispatcher.InvokeAsync(static () => { })
            );
    }

    [Test]
    public async Task Wpf_IndependentDispatchers_TargetTheirOwnThreads()
    {
        using var first = await UiThread.CreateAsync(true);
        using var second = await UiThread.CreateAsync(true);
        (await first.InvokeAsync(() => Environment.CurrentManagedThreadId)).ShouldBe(
            first.ThreadId
        );
        (await second.InvokeAsync(() => Environment.CurrentManagedThreadId)).ShouldBe(
            second.ThreadId
        );
        first.ThreadId.ShouldNotBe(second.ThreadId);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Post_AfterShutdown_FailsExplicitly(bool wpf)
    {
        var host = await UiThread.CreateAsync(wpf);
        host.Dispose();
        host.Dispatcher.CheckAccess().ShouldBeFalse();
        if (wpf)
            Should.Throw<InvalidOperationException>(() => host.Dispatcher.Post(static () => { }));
        else
            Should.Throw<ObjectDisposedException>(() => host.Dispatcher.Post(static () => { }));
    }

    [Test]
    public async Task WinForms_RequiresCreatedHandle_AndRejectsWrongConstructionThread()
    {
        using var host = await UiThread.CreateAsync(false);
        await host.InvokeAsync(() =>
        {
            using var uninitialized = new Forms.Control();
            Should.Throw<InvalidOperationException>(() =>
                new WinFormsConfiglueDispatcher(uninitialized)
            );
            return true;
        });
        Should.Throw<InvalidOperationException>(() =>
            new WinFormsConfiglueDispatcher(host.Anchor!)
        );
    }

    [Test]
    public async Task WinForms_RejectsPostsDuringHandleRecreation_AndResumesAfterward()
    {
        using var host = await UiThread.CreateAsync(false);
        await host.InvokeAsync(() =>
        {
            var anchor = (HandleAnchor)host.Anchor!;
            anchor.DropHandle();
            host.Dispatcher.CheckAccess().ShouldBeFalse();
            Should.Throw<InvalidOperationException>(() => host.Dispatcher.Post(static () => { }));
            anchor.RestoreHandle();
            host.Dispatcher.CheckAccess().ShouldBeTrue();
            return true;
        });
        (await host.InvokeAsync(() => Environment.CurrentManagedThreadId)).ShouldBe(host.ThreadId);
    }

    private sealed class HandleAnchor : Forms.Control
    {
        internal void DropHandle() => DestroyHandle();

        internal void RestoreHandle() => _ = Handle;
    }

    private sealed class UiThread : IDisposable
    {
        private readonly Thread _thread;
        private readonly TaskCompletionSource<bool> _ready = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private Action _stop = static () => { };
        private int _disposed;

        private UiThread(bool wpf)
        {
            _thread = new Thread(() => Run(wpf)) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        internal IConfiglueDispatcher Dispatcher { get; private set; } = null!;
        internal Forms.Control? Anchor { get; private set; }
        internal int ThreadId => _thread.ManagedThreadId;

        internal static async Task<UiThread> CreateAsync(bool wpf)
        {
            var host = new UiThread(wpf);
            await host._ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return host;
        }

        private void Run(bool wpf)
        {
            try
            {
                if (wpf)
                {
                    var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                    Dispatcher = new WpfConfiglueDispatcher(dispatcher);
                    _stop = () => dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                    _ready.SetResult(true);
                    System.Windows.Threading.Dispatcher.Run();
                }
                else
                {
                    using var anchor = new HandleAnchor();
                    _ = anchor.Handle;
                    Anchor = anchor;
                    Dispatcher = new WinFormsConfiglueDispatcher(anchor);
                    _stop = () => anchor.BeginInvoke((Action)Forms.Application.ExitThread);
                    _ready.SetResult(true);
                    Forms.Application.Run();
                }
            }
            catch (Exception exception)
            {
                _ready.TrySetException(exception);
            }
        }

        internal async Task<T> InvokeAsync<T>(Func<T> callback)
        {
            var completion = new TaskCompletionSource<T>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            Dispatcher.Post(() =>
            {
                try
                {
                    completion.SetResult(callback());
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            });
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _stop();
            _thread.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        }
    }

    private sealed class NotificationState
        : IReadOnlyState<DesktopSettings>,
            IConfiglueStateSnapshotRuntime<DesktopSettings>
    {
        private DesktopSettings _value = new() { Counter = 1 };
        private Action<DesktopSettings>? _listener;

        public IDisposable OnChange(Action<DesktopSettings> listener)
        {
            _listener = listener;
            return new Subscription(() => _listener = null);
        }

        public ValueTask<DesktopSettings> GetValueAsync(
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(_value);

        public ValueTask<StateSnapshot<DesktopSettings>> GetSnapshotAsync(
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(new StateSnapshot<DesktopSettings>(_value, null));

        internal void Push(DesktopSettings value)
        {
            _value = value;
            _listener?.Invoke(value);
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

[ConfiglueModel("desktop-host-tests", Version = 1)]
public partial class DesktopSettings
{
    public int Counter { get; set; }
}
