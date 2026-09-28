namespace Configlue;

/// <summary>A staged configuration edit that can be saved multiple times.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public sealed class EditSession<T> : IDisposable
{
    private const int SavingState = 1;
    private const int DisposedState = 2;

    private readonly Func<T, CancellationToken, ValueTask<StateWriteReceipt>> _save;
    private readonly Func<T, T> _clone;
    private readonly T _loadedValue;
    private readonly T _defaultValue;
    private readonly bool _hasLoadedValue;
    private readonly bool _hasDefaultValue;
    private int _state;
    private int _isCommitted;

    /// <summary>Creates a configure session around a staged value and its save operation.</summary>
    /// <remarks>The initial value is the loaded baseline. Default resets require the baseline overload.</remarks>
    public EditSession(T value, Func<T, CancellationToken, ValueTask<StateWriteReceipt>> save)
        : this(value, save, Clone, value, value, hasDefaultValue: false) { }

    /// <summary>Creates a configure session with independent loaded and default baselines.</summary>
    public EditSession(
        T value,
        Func<T, CancellationToken, ValueTask<StateWriteReceipt>> save,
        Func<T, T> clone,
        T loadedValue,
        T defaultValue
    )
        : this(value, save, clone, loadedValue, defaultValue, hasDefaultValue: true) { }

    private EditSession(
        T value,
        Func<T, CancellationToken, ValueTask<StateWriteReceipt>> save,
        Func<T, T> clone,
        T loadedValue,
        T defaultValue,
        bool hasDefaultValue
    )
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _clone = clone ?? throw new ArgumentNullException(nameof(clone));
        _loadedValue = _clone(loadedValue);
        _defaultValue = _clone(defaultValue);
        _hasLoadedValue =
            typeof(T).IsValueType
            || typeof(T) == typeof(string)
            || !ReferenceEquals(loadedValue, _loadedValue);
        _hasDefaultValue = hasDefaultValue;
        Value = _clone(value);
    }

    /// <summary>The editable model value for this session.</summary>
    public T Value { get; set; }

    /// <summary>The editable model value for this session.</summary>
    public T CurrentValue => Value;

    /// <summary>Whether the session has been saved successfully.</summary>
    public bool IsCommitted => Volatile.Read(ref _isCommitted) != 0;

    /// <summary>Updates the editable model value.</summary>
    public void Update(Action<T> updater)
    {
        ArgumentNullException.ThrowIfNull(updater);
        EnsureEditable();
        updater(Value);
    }

    /// <summary>Resets the editable value to the value loaded when this session began.</summary>
    public void ResetToLoaded()
    {
        EnsureEditable();
        EnsureLoadedValue();
        Value = _clone(_loadedValue);
    }

    /// <summary>Resets selected parts of the editable value to the value loaded when this session began.</summary>
    public void ResetToLoaded(Action<T, T> reset)
    {
        ArgumentNullException.ThrowIfNull(reset);
        EnsureEditable();
        EnsureLoadedValue();
        reset(Value, _clone(_loadedValue));
    }

    /// <summary>Resets the editable value to the model's default value.</summary>
    public void ResetToDefault()
    {
        EnsureEditable();
        EnsureDefaultValue();
        Value = _clone(_defaultValue);
    }

    /// <summary>Resets selected parts of the editable value to the model's default value.</summary>
    public void ResetToDefault(Action<T, T> reset)
    {
        ArgumentNullException.ThrowIfNull(reset);
        EnsureEditable();
        EnsureDefaultValue();
        reset(Value, _clone(_defaultValue));
    }

    /// <summary>Commits the edited value against the latest resolved source state.</summary>
    public async ValueTask<StateWriteReceipt> CommitAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (Interlocked.CompareExchange(ref _state, SavingState, 0) != 0)
        {
            throw new InvalidOperationException(
                "This configure session is already saving or disposed."
            );
        }

        try
        {
            var result = await _save(_clone(Value), cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _isCommitted, 1);
            CompleteSave();
            return result;
        }
        catch
        {
            CompleteSave();
            throw;
        }
    }

    /// <summary>Discards this session without saving it.</summary>
    /// <remarks>A save already in progress is allowed to finish, then the session becomes disposed.</remarks>
    public void Dispose()
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if ((state & DisposedState) != 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _state, state | DisposedState, state) == state)
            {
                return;
            }
        }
    }

    private void EnsureEditable()
    {
        if (Volatile.Read(ref _state) != 0)
        {
            throw new InvalidOperationException(
                "This configure session is already saving or disposed."
            );
        }
    }

    private void EnsureDefaultValue()
    {
        if (!_hasDefaultValue)
        {
            throw new InvalidOperationException(
                "This configure session was not given a model-default baseline."
            );
        }
    }

    private void EnsureLoadedValue()
    {
        if (!_hasLoadedValue)
        {
            throw new InvalidOperationException(
                "This configure session's loaded baseline could not be cloned."
            );
        }
    }

    private void CompleteSave()
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            var completedState = state & ~SavingState;
            if (Interlocked.CompareExchange(ref _state, completedState, state) == state)
            {
                return;
            }
        }
    }

    private static T Clone(T value) =>
        value is IConfiglueDeepCloneable<T> deepCloneable ? deepCloneable.DeepClone() : value;
}
