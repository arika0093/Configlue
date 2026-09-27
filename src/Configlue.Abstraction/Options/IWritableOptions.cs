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

    /// <summary>Applies a generated sparse patch to the configured write source's fragment.</summary>
    ValueTask<StateWriteResult> SaveAsync(
        IConfigluePatch patch,
        CancellationToken cancellationToken = default
    );
}
