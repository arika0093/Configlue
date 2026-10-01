using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Hosting.Blazor;

/// <summary>
/// Headless component that resolves a read-only configuration snapshot and exposes it to child content.
/// </summary>
/// <typeparam name="T">The configuration model type.</typeparam>
/// <remarks>
/// Subscribes to <see cref="IReadOnlyState{T}.OnChange"/> so effective upstream changes re-resolve the
/// snapshot. Reload failures never replace the last successfully rendered value.
/// </remarks>
public sealed partial class StateReader<T> : ComponentBase, IDisposable
{
    private readonly StateReaderContext<T> _context;
    private IDisposable? _changeSubscription;
    private IDisposable? _reloadFailureSubscription;
    private int _disposed;

    /// <summary>Creates a reader component.</summary>
    public StateReader() => _context = new StateReaderContext<T>(this);

    /// <summary>The service provider used for optional diagnostics resolution.</summary>
    [Inject]
    public IServiceProvider Services { get; set; } = default!;

    /// <summary>The state view that resolves snapshots for this component.</summary>
    [Inject]
    public IReadOnlyState<T> State { get; set; } = default!;

    /// <summary>Child content rendered with the current read context.</summary>
    [Parameter]
    public RenderFragment<StateReaderContext<T>>? ChildContent { get; set; }

    /// <summary>Content rendered while the initial snapshot is loading.</summary>
    [Parameter]
    public RenderFragment? LoadingContent { get; set; }

    /// <summary>Content rendered when the initial snapshot fails to load.</summary>
    [Parameter]
    public RenderFragment<Exception>? LoadFailedContent { get; set; }

    /// <summary>Raised when the background watcher reports a reload failure.</summary>
    [Parameter]
    public EventCallback<Exception> OnReloadFailed { get; set; }

    internal StateSnapshot<T>? Snapshot { get; private set; }

    internal bool IsLoading { get; private set; } = true;

    internal Exception? LoadFailure { get; private set; }

    internal Exception? ReloadFailure { get; private set; }

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        _changeSubscription = State.OnChange(OnStateChanged);
        if (
            Services.GetService<IConfiglueDiagnostics<T>>()
            is IConfiglueReloadFailureDiagnostics<T> diagnostics
        )
        {
            _reloadFailureSubscription = diagnostics.OnReloadFailed(OnReloadFailureReported);
        }

        await ReloadAsync().ConfigureAwait(true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _changeSubscription?.Dispose();
        _reloadFailureSubscription?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnStateChanged(T value)
    {
        _ = value;
        _ = InvokeAsync(ReloadAsync);
    }

    private void OnReloadFailureReported(Exception exception)
    {
        ReloadFailure = exception;
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _ = InvokeAsync(async () =>
        {
            await OnReloadFailed.InvokeAsync(exception).ConfigureAwait(true);
            StateHasChanged();
        });
    }

    private async Task ReloadAsync()
    {
        try
        {
            var snapshot = await State.GetSnapshotAsync().ConfigureAwait(true);
            Snapshot = snapshot;
            ReloadFailure = null;
            LoadFailure = null;
        }
        catch (Exception exception)
        {
            if (Snapshot is null)
            {
                LoadFailure = exception;
            }
            else
            {
                ReloadFailure = exception;
            }
        }
        finally
        {
            IsLoading = false;
            if (Volatile.Read(ref _disposed) == 0)
            {
                StateHasChanged();
            }
        }
    }
}
