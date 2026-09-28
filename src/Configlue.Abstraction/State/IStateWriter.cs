namespace Configlue.State;

/// <summary>Writes state to a backend that supports updates.</summary>
public interface IStateWriter<T>
{
    /// <summary>Writes state, optionally requiring the backend revision to match.</summary>
    ValueTask<StateWriteResult> WriteAsync(
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    );
}
