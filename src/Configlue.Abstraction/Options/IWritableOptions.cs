namespace Configlue;

/// <summary>Reads and saves a configuration value.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public interface IWritableOptions<T> : IReadOnlyOptions<T>
{
    /// <summary>Begins editing a deep clone of the currently resolved configuration.</summary>
    ValueTask<ConfigureSession<T>> BeginConfigureAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves a complete configuration value to the configured write source.</summary>
    ValueTask<StateWriteResult> SaveAsync(T value, CancellationToken cancellationToken = default);

    /// <summary>Updates a deep clone of the current configuration and saves it.</summary>
    ValueTask<StateWriteResult> SaveAsync(Action<T> update, CancellationToken cancellationToken = default);

    /// <summary>Asynchronously updates a deep clone of the current configuration and saves it.</summary>
    ValueTask<StateWriteResult> SaveAsync(Func<T, Task> update, CancellationToken cancellationToken = default);

    /// <summary>Applies a generated set/unset patch to the configured write source's fragment.</summary>
    ValueTask<StateWriteResult> ApplyPatchAsync(IConfigluePatch patch, CancellationToken cancellationToken = default);
}
