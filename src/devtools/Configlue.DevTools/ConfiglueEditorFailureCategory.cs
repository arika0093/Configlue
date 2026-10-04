namespace Configlue.DevTools;

/// <summary>
/// Semantic categories for DevTools editor failures.
/// </summary>
/// <remarks>
/// <para>
/// Development-only. Every rejected draft, failed validation, routing problem,
/// conflict, partial write, or invalidated session is reported with one of these
/// categories so the UI never hides the underlying semantic cause behind a plain
/// text error.
/// </para>
/// <para>
/// Browser-side Monaco JSON-schema feedback (syntax, enums, constraints) stays
/// advisory; <see cref="Validation"/> and the other categories below always come
/// from the server-side Configlue pipeline.
/// </para>
/// </remarks>
public enum ConfiglueEditorFailureCategory
{
    /// <summary>No failure.</summary>
    None,

    /// <summary>The draft is not well-formed JSON or is not a JSON object.</summary>
    Parse,

    /// <summary>
    /// The draft parses but violates the model shape (unknown members, wrong types,
    /// null where a value is required).
    /// </summary>
    Schema,

    /// <summary>Normal Configlue model/runtime validation rejected the draft.</summary>
    Validation,

    /// <summary>
    /// The draft changes a member that cannot be changed through the normal logical
    /// save path (read-only, shadowed, or without a write target).
    /// </summary>
    NonEditable,

    /// <summary>
    /// The draft smuggles secret plaintext (or deletes a secret placeholder) into the
    /// Monaco text model. Secrets change only through the explicit secret-change flow.
    /// </summary>
    Secret,

    /// <summary>Write routing could not realize the requested edit.</summary>
    Routing,

    /// <summary>An optimistic-concurrency or rebase conflict was detected.</summary>
    Conflict,

    /// <summary>A multi-source write failed after partial completion.</summary>
    PartialWrite,

    /// <summary>
    /// The subject/state identity is gone: the session was disposed, the circuit-side
    /// state was torn down, or a stale draft targeted a different selection.
    /// </summary>
    Invalidated,
}
