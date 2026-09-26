namespace Configlue;

/// <summary>A staged configuration edit that can be saved once.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public sealed class ConfigureSession<T> : IDisposable
{
    private readonly Func<T, CancellationToken, ValueTask<StateWriteResult>> _save;
    private int _state;

    /// <summary>Creates a configure session around a staged value and its save operation.</summary>
    public ConfigureSession(T value, Func<T, CancellationToken, ValueTask<StateWriteResult>> save)
    {
        Value = value;
        _save = save ?? throw new ArgumentNullException(nameof(save));
    }

    /// <summary>The editable model value for this session.</summary>
    public T Value { get; set; }

    /// <summary>Whether the session has been saved successfully.</summary>
    public bool IsCommitted => Volatile.Read(ref _state) == 2;

    /// <summary>Saves the edited value using the revision captured when the session began.</summary>
    public async ValueTask<StateWriteResult> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            throw new InvalidOperationException("This configure session is already saving, committed, or disposed.");
        }

        try
        {
            var result = await _save(Value, cancellationToken).ConfigureAwait(false);
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
}
