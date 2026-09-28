namespace Configlue;

/// <summary>Reads resolved state and generated provenance details.</summary>
/// <typeparam name="T">The configuration model.</typeparam>
public interface IConfiglueInspection<T>
{
    /// <summary>Reads the resolved value together with state and revision metadata.</summary>
    ValueTask<StateReadResult<T>> ReadAsync(CancellationToken cancellationToken = default);
}
