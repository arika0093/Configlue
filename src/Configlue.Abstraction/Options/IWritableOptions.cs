namespace Configlue;

/// <summary>Reads and saves a configuration value.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public interface IWritableOptions<T> : IReadOnlyOptions<T>
{
    /// <summary>Begins editing a deep clone of the currently resolved configuration.</summary>
    ValueTask<EditSession<T>> OpenEditSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>Begins editing with path-based source routing for changed model members.</summary>
    ValueTask<EditSession<T>> OpenEditSessionAsync(
        StateWritePlan writePlan,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Replaces the configured write source's contribution with a complete configuration value.
    /// Missing members in that source are not preserved; use an update delegate, configure session, or patch for sparse edits.
    /// </summary>
    ValueTask<StateWriteResult> SaveAsync(T value, CancellationToken cancellationToken = default);

    /// <summary>Saves semantic changes to a model value using path-based source routing.</summary>
    ValueTask<StateWriteResult> SaveAsync(
        T value,
        StateWritePlan writePlan,
        CancellationToken cancellationToken = default
    );

    /// <summary>Updates a deep clone of the current configuration and saves it.</summary>
    ValueTask<StateWriteResult> SaveAsync(
        Action<T> update,
        CancellationToken cancellationToken = default
    );

    /// <summary>Updates a clone of the current value and saves changed paths to their planned sources.</summary>
    ValueTask<StateWriteResult> SaveAsync(
        Action<T> update,
        StateWritePlan writePlan,
        CancellationToken cancellationToken = default
    );

    /// <summary>Asynchronously updates a deep clone of the current configuration and saves it.</summary>
    ValueTask<StateWriteResult> SaveAsync(
        Func<T, Task> update,
        CancellationToken cancellationToken = default
    );

    /// <summary>Asynchronously updates a clone of the current value using path-based source routing.</summary>
    ValueTask<StateWriteResult> SaveAsync(
        Func<T, Task> update,
        StateWritePlan writePlan,
        CancellationToken cancellationToken = default
    );

    /// <summary>Applies a generated set/unset patch to the configured write source's fragment.</summary>
    ValueTask<StateWriteResult> ApplyPatchAsync(
        IConfigluePatch patch,
        CancellationToken cancellationToken = default
    );
}
