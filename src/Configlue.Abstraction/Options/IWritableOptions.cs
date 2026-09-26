namespace Configlue;

/// <summary>Reads and saves a configuration value.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public interface IWritableOptions<T> : IReadOnlyOptions<T>
{
    /// <summary>Begins editing a deep clone of the currently resolved configuration.</summary>
    ValueTask<ConfigureSession<T>> BeginConfigureAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves a complete configuration value to the configured write source.</summary>
    ValueTask<StateWriteResult> SaveAsync(T value, CancellationToken cancellationToken = default);
}
