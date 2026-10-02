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
    private long _generation;

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

        await ReloadAsync(Interlocked.Increment(ref _generation)).ConfigureAwait(true);
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
        Interlocked.Increment(ref _generation);
        GC.SuppressFinalize(this);
    }

    private void OnStateChanged(T value)
    {
        _ = value;
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _generation);
        _ = InvokeAsync(() => ReloadAsync(generation));
    }

    private void OnReloadFailureReported(Exception exception)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var generation = Interlocked.Read(ref _generation);
        _ = InvokeAsync(async () =>
        {
            if (!IsCurrent(generation))
            {
                return;
            }

            ReloadFailure = exception;
            await OnReloadFailed.InvokeAsync(exception).ConfigureAwait(true);
            if (IsCurrent(generation))
            {
                StateHasChanged();
            }
        });
    }

    private async Task ReloadAsync(long generation)
    {
        if (!IsCurrent(generation))
        {
            return;
        }
        try
        {
            var snapshot = await State.GetSnapshotAsync().ConfigureAwait(true);
            if (!IsCurrent(generation))
            {
                return;
            }

            Snapshot = snapshot;
            ReloadFailure = null;
            LoadFailure = null;
        }
        catch (Exception exception)
        {
            if (!IsCurrent(generation))
            {
                return;
            }

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
            if (IsCurrent(generation))
            {
                IsLoading = false;
                StateHasChanged();
            }
        }
    }

    private bool IsCurrent(long generation) =>
        Volatile.Read(ref _disposed) == 0 && generation == Interlocked.Read(ref _generation);
}
