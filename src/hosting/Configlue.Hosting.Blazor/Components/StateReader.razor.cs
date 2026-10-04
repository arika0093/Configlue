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
/// <para>
/// When <see cref="PersistPrerenderedState"/> is enabled (the default) and Blazor
/// <see cref="PersistentComponentState"/> is available, the prerendered value is persisted at the
/// end of prerender and restored immediately when the interactive renderer starts, so the first
/// interactive render shows the prerendered value instead of a loading placeholder. The
/// interactive source is always re-read afterwards, so a stale persisted value can only seed the
/// first frame and never suppress a fresh read.
/// </para>
/// </remarks>
public sealed partial class StateReader<T> : ComponentBase, IDisposable
{
    private readonly StateReaderContext<T> _context;
    private IDisposable? _changeSubscription;
    private IDisposable? _reloadFailureSubscription;
    private IDisposable? _persistSubscription;
    private IPrerenderSnapshotStore? _prerenderStore;
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

    /// <summary>
    /// Whether the prerendered snapshot hands off to the interactive renderer through Blazor
    /// <see cref="PersistentComponentState"/>. Defaults to <c>true</c>; automatically inert when
    /// persistent component state is unavailable (for example in unit tests).
    /// </summary>
    [Parameter]
    public bool PersistPrerenderedState { get; set; } = true;

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

        RestorePrerenderedSnapshot();
        await ReloadAsync(Interlocked.Increment(ref _generation)).ConfigureAwait(true);
    }

    internal static string PersistenceKeyFor<TModel>() =>
        "configlue:state-reader:" + (typeof(TModel).FullName ?? typeof(TModel).Name);

    private void RestorePrerenderedSnapshot()
    {
        if (!PersistPrerenderedState || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var store =
            Services.GetService<IPrerenderSnapshotStore>()
            ?? (
                Services.GetService<PersistentComponentState>() is { } componentState
                    ? new PersistentComponentStateStore(componentState)
                    : null
            );
        if (store is null)
        {
            return;
        }

        _prerenderStore = store;
        _persistSubscription = store.OnPersisting(PersistSnapshotAsync);

        try
        {
            if (store.TryTake<T>(PersistenceKeyFor<T>(), out var restored) && restored is not null)
            {
                Snapshot = new StateSnapshot<T>(restored, details: null);
                IsLoading = false;
            }
        }
        catch (Exception)
        {
            // A stale or incompatible payload must fall through to a fresh read.
        }
    }

    private Task PersistSnapshotAsync()
    {
        try
        {
            if (_prerenderStore is { } store && Snapshot is { } snapshot)
            {
                store.Persist(PersistenceKeyFor<T>(), snapshot.Value);
            }
        }
        catch (Exception)
        {
            // Persistence must never fail rendering.
        }

        return Task.CompletedTask;
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
        _persistSubscription?.Dispose();
        _prerenderStore = null;
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
