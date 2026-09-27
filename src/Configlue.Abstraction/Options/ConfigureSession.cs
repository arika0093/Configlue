namespace Configlue;

/// <summary>A staged configuration edit that can be saved once.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public sealed class ConfigureSession<T> : IDisposable
{
    private readonly Func<T, CancellationToken, ValueTask<StateWriteResult>> _save;
    private readonly Func<T, T> _clone;
    private readonly T _loadedValue;
    private readonly T _defaultValue;
    private int _state;

    /// <summary>Creates a configure session around a staged value and its save operation.</summary>
    public ConfigureSession(T value, Func<T, CancellationToken, ValueTask<StateWriteResult>> save)
        : this(value, save, Clone, value, value) { }

    /// <summary>Creates a configure session with independent loaded and default baselines.</summary>
    public ConfigureSession(
        T value,
        Func<T, CancellationToken, ValueTask<StateWriteResult>> save,
        Func<T, T> clone,
        T loadedValue,
        T defaultValue
    )
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _clone = clone ?? throw new ArgumentNullException(nameof(clone));
        _loadedValue = _clone(loadedValue);
        _defaultValue = _clone(defaultValue);
        Value = _clone(value);
    }

    /// <summary>The editable model value for this session.</summary>
    public T Value { get; set; }

    /// <summary>The editable model value for this session.</summary>
    public T CurrentValue => Value;

    /// <summary>Whether the session has been saved successfully.</summary>
    public bool IsCommitted => Volatile.Read(ref _state) == 2;

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
        Value = _clone(_loadedValue);
    }

    /// <summary>Resets selected parts of the editable value to the value loaded when this session began.</summary>
    public void ResetToLoaded(Action<T, T> reset)
    {
        ArgumentNullException.ThrowIfNull(reset);
        EnsureEditable();
        reset(Value, _clone(_loadedValue));
    }

    /// <summary>Resets the editable value to the model's default value.</summary>
    public void ResetToDefault()
    {
        EnsureEditable();
        Value = _clone(_defaultValue);
    }

    /// <summary>Resets selected parts of the editable value to the model's default value.</summary>
    public void ResetToDefault(Action<T, T> reset)
    {
        ArgumentNullException.ThrowIfNull(reset);
        EnsureEditable();
        reset(Value, _clone(_defaultValue));
    }

    /// <summary>Saves the edited value using the revision captured when the session began.</summary>
    public async ValueTask<StateWriteResult> SaveAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "This configure session is already saving, committed, or disposed."
            );
        }

        try
        {
            var result = await _save(_clone(Value), cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _state, 2);
            return result;
        }
        catch
        {
            Volatile.Write(ref _state, 0);
            throw;
        }
    }

    /// <summary>Discards this session without saving it.</summary>
    public void Dispose() => Interlocked.CompareExchange(ref _state, 3, 0);

    private void EnsureEditable()
    {
        if (Volatile.Read(ref _state) != 0)
        {
            throw new InvalidOperationException(
                "This configure session is already saving, committed, or disposed."
            );
        }
    }

    private static T Clone(T value) =>
        value is IConfiglueDeepCloneable<T> deepCloneable ? deepCloneable.DeepClone() : value;
}
