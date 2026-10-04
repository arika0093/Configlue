namespace Configlue;

/// <summary>Opens long-lived drafts of resolved configuration.</summary>
/// <remarks>Advanced application API for draft editing.</remarks>
/// <typeparam name="T">The configuration model.</typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueEditSessions<T>
{
    /// <summary>Begins editing a deep clone of the currently resolved configuration.</summary>
    ValueTask<EditSession<T>> OpenEditSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>Begins editing with path-based source routing for changed model members.</summary>
    ValueTask<EditSession<T>> OpenEditSessionAsync(
        StateWritePlan writePlan,
        CancellationToken cancellationToken = default
    );
}
