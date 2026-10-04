namespace Configlue;

/// <summary>Identifies an application-defined scope for configuration state.</summary>
/// <remarks>Configlue uses only <see cref="Key"/> for logical identity.
/// Advanced application API for subject-scoped state.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueSubject
{
    /// <summary>The canonical logical key for this subject.</summary>
    SubjectKey Key { get; }
}
