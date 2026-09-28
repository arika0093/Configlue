namespace Configlue.Testing;

/// <summary>An in-memory typed state source for runtime and application tests.</summary>
public sealed class InMemoryStateStore<T> : IStateReader<T>, IStateWriter<T>, IStateWatcher
{
    private readonly object _gate = new();
    private StateReadStatus _status;
    private T? _value;
    private string? _revision;
    private long _revisionNumber;
    private TaskCompletionSource _changed = NewSignal();

    /// <summary>Creates an empty state store.</summary>
    public InMemoryStateStore() => _status = StateReadStatus.NotFound;

    /// <summary>Creates a state store with an initial value.</summary>
    public InMemoryStateStore(T? initialValue)
    {
        _value = initialValue;
        _status = StateReadStatus.Success;
        _revision = "1";
        _revisionNumber = 1;
    }

    /// <inheritdoc />
    public ValueTask<StateReadResult<T>> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(
                _status switch
                {
                    StateReadStatus.Success => StateReadResult<T>.Success(_value, _revision),
                    StateReadStatus.NotFound => StateReadResult<T>.NotFound(_revision),
                    _ => StateReadResult<T>.Unavailable(_revision),
                }
            );
        }
    }

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource changed;
        string revision;
        lock (_gate)
        {
            if (!request.Condition.IsSatisfiedBy(_revision, _status != StateReadStatus.NotFound))
            {
                throw new StateConflictException("The in-memory state changed after it was read.");
            }

            _value = request.Value;
            _status = StateReadStatus.Success;
            _revision = revision = (++_revisionNumber).ToString(
                System.Globalization.CultureInfo.InvariantCulture
            );
            changed = _changed;
            _changed = NewSignal();
        }

        changed.TrySetResult();
        return ValueTask.FromResult(new StateWriteResult(revision));
    }

    /// <summary>Changes this source to report temporary unavailability.</summary>
    public void SetUnavailable()
    {
        Signal(() =>
        {
            _status = StateReadStatus.Unavailable;
            _value = default;
            _revision = (++_revisionNumber).ToString(
                System.Globalization.CultureInfo.InvariantCulture
            );
        });
    }

    /// <summary>Changes this source to report that no value exists.</summary>
    public void SetNotFound()
    {
        Signal(() =>
        {
            _status = StateReadStatus.NotFound;
            _value = default;
            _revision = (++_revisionNumber).ToString(
                System.Globalization.CultureInfo.InvariantCulture
            );
        });
    }

    /// <summary>Replaces the current value and notifies watchers.</summary>
    public void Set(T? value)
    {
        Signal(() =>
        {
            _value = value;
            _status = StateReadStatus.Success;
            _revision = (++_revisionNumber).ToString(
                System.Globalization.CultureInfo.InvariantCulture
            );
        });
    }

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        Task waitTask;
        lock (_gate)
        {
            if (!string.Equals(_revision, observedRevision, StringComparison.Ordinal))
            {
                return;
            }

            waitTask = _changed.Task;
        }

        await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Signal(Action update)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            update();
            changed = _changed;
            _changed = NewSignal();
        }

        changed.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
