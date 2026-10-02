using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace Configlue.Extensions.ComponentModel;

/// <summary>
/// A bindable, host-neutral read-only view over an <see cref="IReadOnlyState{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The reader resolves each snapshot as a whole (value plus provenance/editability) so
/// bindings never observe a value that disagrees with its details. A failed reload keeps
/// the last known-good value and only surfaces <see cref="ReloadFailure"/>, matching the
/// Blazor <c>StateReader&lt;T&gt;</c> semantics.
/// </para>
/// <para>
/// Incoming effective-state changes and reload-failure notifications may arrive on
/// background threads; they are marshaled through the supplied
/// <see cref="IConfiglueDispatcher"/>. Asynchronous operations complete only after their
/// bindable state transition has been applied on the dispatcher, and reload generations
/// ensure an older snapshot can never overwrite a newer one.
/// </para>
/// </remarks>
/// <typeparam name="T">The configuration model type.</typeparam>
public sealed class ConfiglueStateReader<T> : INotifyPropertyChanged, IDisposable
{
    private readonly IReadOnlyState<T> _state;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly CancellationToken _lifetimeToken;
    private Task? _initializationTask;
    private readonly IConfiglueDispatcher _dispatcher;
    private readonly IConfiglueDiagnostics<T>? _diagnostics;
    private IDisposable? _changeSubscription;
    private IDisposable? _reloadFailureSubscription;
    private StateSnapshot<T>? _snapshot;
    private object? _value;
    private bool _isLoading = true;
    private Exception? _loadFailure;
    private Exception? _reloadFailure;
    private int _generation;
    private bool _disposed;

    /// <summary>Creates a read-only bindable reader.</summary>
    /// <param name="state">The state view that resolves snapshots for this reader.</param>
    /// <param name="dispatcher">
    /// The dispatcher used to marshal notifications. Defaults to the current
    /// <see cref="SynchronizationContext"/> when present.
    /// </param>
    /// <param name="diagnostics">Optional diagnostics used to observe reload failures.</param>
    public ConfiglueStateReader(
        IReadOnlyState<T> state,
        IConfiglueDispatcher? dispatcher = null,
        IConfiglueDiagnostics<T>? diagnostics = null
    )
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _dispatcher = dispatcher ?? ConfiglueDispatcher.FromCurrentSynchronizationContext();
        _lifetimeToken = _lifetimeCancellation.Token;
        _diagnostics = diagnostics;
    }

    /// <summary>The most recently resolved snapshot, or <see langword="null"/> before the first successful load.</summary>
    public StateSnapshot<T>? Snapshot => _snapshot;

    /// <summary>
    /// The bindable value. When the model has a generated proxy this is an observable
    /// proxy; otherwise it is the raw snapshot value.
    /// </summary>
    public object? Value => _value;

    /// <summary>Whether the initial snapshot is still loading.</summary>
    public bool IsLoading => _isLoading;

    /// <summary>Whether a snapshot has been resolved at least once.</summary>
    public bool HasValue => _snapshot is not null;

    /// <summary>The failure that prevented the initial snapshot from loading.</summary>
    public Exception? LoadFailure => _loadFailure;

    /// <summary>The failure that prevented the most recent reload; the last known-good value is retained.</summary>
    public Exception? ReloadFailure => _reloadFailure;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Subscribes to state changes and performs the initial load.</summary>
    /// <param name="cancellationToken">A token that can cancel the initial load.</param>
    /// <returns>A task that completes when the initial state transition has been applied.</returns>
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initializationTask is null)
            {
                var completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                _initializationTask = completion.Task;
                _ = InitializeCoreAsync(completion, cancellationToken);
            }
            return new ValueTask(_initializationTask);
        }
    }

    private async Task InitializeCoreAsync(
        TaskCompletionSource<bool> completion,
        CancellationToken cancellationToken
    )
    {
        try
        {
            _changeSubscription = _state.OnChange(OnStateChanged);
            if (_diagnostics is IConfiglueReloadFailureDiagnostics<T> diagnostics)
                _reloadFailureSubscription = diagnostics.OnReloadFailed(OnReloadFailureReported);
            await ReloadAsync(cancellationToken).ConfigureAwait(false);
            completion.TrySetResult(true);
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        IDisposable? changes;
        IDisposable? failures;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            Interlocked.Increment(ref _generation);
            changes = _changeSubscription;
            failures = _reloadFailureSubscription;
            _changeSubscription = null;
            _reloadFailureSubscription = null;
        }
        _lifetimeCancellation.Cancel();
        changes?.Dispose();
        failures?.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private void OnStateChanged(T value)
    {
        // The payload is deliberately ignored: re-resolving keeps value and details consistent.
        _ = value;
        if (_disposed)
        {
            return;
        }

        _ = ReloadFromNotificationAsync();
    }

    private async Task ReloadFromNotificationAsync()
    {
        try
        {
            await ReloadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) when (_lifetimeToken.IsCancellationRequested)
        {
            // Disposal cancels pending notification work.
        }
    }

    private void OnReloadFailureReported(Exception exception)
    {
        if (_disposed)
        {
            return;
        }

        RunOnDispatcher(() =>
        {
            if (_disposed)
            {
                return;
            }

            _reloadFailure = exception;
            Raise(nameof(ReloadFailure));
        });
    }

    private async ValueTask ReloadAsync(CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _generation);
        StateSnapshot<T>? snapshot = null;
        Exception? failure = null;
        try
        {
            snapshot = await _state.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        await InvokeAsync(
                () =>
                {
                    if (_disposed || generation != Volatile.Read(ref _generation))
                    {
                        return;
                    }

                    if (failure is null)
                    {
                        _snapshot = snapshot;
                        _value = ConfiglueBindableProxy.Create(typeof(T), snapshot!.Value!, null);
                        _loadFailure = null;
                        _reloadFailure = null;
                        Raise(nameof(Snapshot));
                        Raise(nameof(Value));
                        Raise(nameof(HasValue));
                        Raise(nameof(LoadFailure));
                        Raise(nameof(ReloadFailure));
                    }
                    else if (_snapshot is null)
                    {
                        _loadFailure = failure;
                        Raise(nameof(LoadFailure));
                    }
                    else
                    {
                        _reloadFailure = failure;
                        Raise(nameof(ReloadFailure));
                    }

                    if (_isLoading)
                    {
                        _isLoading = false;
                        Raise(nameof(IsLoading));
                    }
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async ValueTask InvokeAsync(Action action, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeToken
        );
        await _dispatcher
            .InvokeAsync(
                () =>
                {
                    lock (_gate)
                        if (!_disposed)
                            action();
                },
                linked.Token
            )
            .ConfigureAwait(false);
    }

    private void RunOnDispatcher(Action action)
    {
        void GuardedAction()
        {
            lock (_gate)
                if (!_disposed)
                    action();
        }
        if (_dispatcher.CheckAccess())
            GuardedAction();
        else
            _dispatcher.Post(GuardedAction);
    }

    private void Raise(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
