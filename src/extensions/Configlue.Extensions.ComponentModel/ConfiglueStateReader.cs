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
/// <see cref="IConfiglueDispatcher"/>.
/// </para>
/// </remarks>
/// <typeparam name="T">The configuration model type.</typeparam>
public sealed class ConfiglueStateReader<T> : INotifyPropertyChanged, IDisposable
{
    private readonly IReadOnlyState<T> _state;
    private readonly IConfiglueDispatcher _dispatcher;
    private readonly IConfiglueDiagnostics<T>? _diagnostics;
    private IDisposable? _changeSubscription;
    private IDisposable? _reloadFailureSubscription;
    private StateSnapshot<T>? _snapshot;
    private object? _value;
    private bool _isLoading = true;
    private Exception? _loadFailure;
    private Exception? _reloadFailure;
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
    /// <returns>A task that completes when the initial load finishes.</returns>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_changeSubscription is not null)
        {
            return;
        }

        _changeSubscription = _state.OnChange(OnStateChanged);
        if (_diagnostics is IConfiglueReloadFailureDiagnostics<T> diagnostics)
        {
            _reloadFailureSubscription = diagnostics.OnReloadFailed(OnReloadFailureReported);
        }

        await ReloadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _changeSubscription?.Dispose();
        _reloadFailureSubscription?.Dispose();
        _changeSubscription = null;
        _reloadFailureSubscription = null;
    }

    private void OnStateChanged(T value)
    {
        // The payload is deliberately ignored: re-resolving keeps value and details consistent.
        _ = value;
        if (_disposed)
        {
            return;
        }

        RunOnDispatcher(() => _ = ReloadAsync(CancellationToken.None));
    }

    private void OnReloadFailureReported(Exception exception)
    {
        if (_disposed)
        {
            return;
        }

        RunOnDispatcher(() =>
        {
            _reloadFailure = exception;
            Raise(nameof(ReloadFailure));
        });
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _state.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            RunOnDispatcher(() =>
            {
                _snapshot = snapshot;
                _value = ConfiglueBindableProxy.Create(typeof(T), snapshot.Value!, null);
                _loadFailure = null;
                _reloadFailure = null;
                Raise(nameof(Snapshot));
                Raise(nameof(Value));
                Raise(nameof(HasValue));
                Raise(nameof(LoadFailure));
                Raise(nameof(ReloadFailure));
            });
        }
        catch (Exception exception)
        {
            RunOnDispatcher(() =>
            {
                if (_snapshot is null)
                {
                    _loadFailure = exception;
                }
                else
                {
                    _reloadFailure = exception;
                }

                Raise(nameof(LoadFailure));
                Raise(nameof(ReloadFailure));
            });
        }
        finally
        {
            RunOnDispatcher(() =>
            {
                _isLoading = false;
                Raise(nameof(IsLoading));
            });
        }
    }

    private void RunOnDispatcher(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.Post(action);
        }
    }

    private void Raise(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
