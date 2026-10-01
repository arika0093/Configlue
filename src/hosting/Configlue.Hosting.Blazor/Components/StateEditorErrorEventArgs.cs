namespace Configlue.Hosting.Blazor;

/// <summary>The editor operation that produced an error.</summary>
public enum StateEditorOperation
{
    /// <summary>The initial edit session failed to open.</summary>
    Load = 0,

    /// <summary>A save/commit operation failed.</summary>
    Save = 1,

    /// <summary>A rebase operation failed.</summary>
    Rebase = 2,

    /// <summary>A reset operation failed.</summary>
    Reset = 3,

    /// <summary>A save was blocked because the current subject changed.</summary>
    SubjectChange = 4,

    /// <summary>The background watcher reported an upstream reload failure.</summary>
    Reload = 5,
}

/// <summary>A categorized classification of an editor error, retaining the underlying Core exception.</summary>
public enum StateEditorErrorKind
{
    /// <summary>Configlue state validation failed.</summary>
    Validation = 0,

    /// <summary>A conditional write conflicted with a concurrent revision.</summary>
    Conflict = 1,

    /// <summary>A multi-source write failed after partial completion.</summary>
    MultiWrite = 2,

    /// <summary>A read/snapshot/reload failure occurred.</summary>
    Reload = 3,

    /// <summary>A save was blocked because the current subject changed while the form was dirty.</summary>
    SubjectChanged = 4,

    /// <summary>An uncategorized failure occurred.</summary>
    Unknown = 5,
}

/// <summary>Describes a categorized editor failure while retaining the underlying Core exception.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
// The model parameter identifies the event args in typed component callbacks.
#pragma warning disable S2326
public sealed class StateEditorErrorEventArgs<T> : EventArgs
{
    /// <summary>Creates categorized editor error information.</summary>
    /// <param name="operation">The operation that failed.</param>
    /// <param name="kind">The categorized error kind.</param>
    /// <param name="exception">The underlying Core exception.</param>
    public StateEditorErrorEventArgs(
        StateEditorOperation operation,
        StateEditorErrorKind kind,
        Exception exception
    )
    {
        ArgumentNullException.ThrowIfNull(exception);
        Operation = operation;
        Kind = kind;
        Exception = exception;
    }

    /// <summary>The operation that failed.</summary>
    public StateEditorOperation Operation { get; }

    /// <summary>The categorized error kind.</summary>
    public StateEditorErrorKind Kind { get; }

    /// <summary>The underlying Core exception; never flattened to a string.</summary>
    public Exception Exception { get; }

    /// <summary>The Core validation failure, when <see cref="Kind"/> is <see cref="StateEditorErrorKind.Validation"/>.</summary>
    public ConfiglueValidationException? ValidationException =>
        Exception as ConfiglueValidationException;

    /// <summary>The Core write conflict, when <see cref="Kind"/> is <see cref="StateEditorErrorKind.Conflict"/>.</summary>
    public StateConflictException? ConflictException => Exception as StateConflictException;

    /// <summary>The Core partial-write failure, when <see cref="Kind"/> is <see cref="StateEditorErrorKind.MultiWrite"/>.</summary>
    public StateMultiWriteException? MultiWriteException => Exception as StateMultiWriteException;
}
#pragma warning restore S2326

/// <summary>Describes a change of the current configuration subject for an open editor.</summary>
public sealed class StateEditorSubjectChangedEventArgs : EventArgs
{
    /// <summary>Creates subject-change information.</summary>
    /// <param name="hasUnsavedChanges">Whether the editor held a dirty draft when the subject changed.</param>
    public StateEditorSubjectChangedEventArgs(bool hasUnsavedChanges) =>
        HasUnsavedChanges = hasUnsavedChanges;

    /// <summary>Whether the editor held a dirty draft when the subject changed.</summary>
    public bool HasUnsavedChanges { get; }
}
