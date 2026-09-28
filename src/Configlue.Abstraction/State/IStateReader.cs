namespace Configlue.State;

/// <summary>Reads the current logical state from a backend.</summary>
public interface IStateReader<T>
{
    /// <summary>Reads the current state.</summary>
    ValueTask<StateReadResult<T>> ReadAsync(CancellationToken cancellationToken = default);
}
