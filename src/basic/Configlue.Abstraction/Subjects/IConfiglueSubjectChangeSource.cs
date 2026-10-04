namespace Configlue;

/// <summary>Optionally reports when a scoped accessor should resolve its current subject again.</summary>
/// <remarks>Advanced application API for subject-scoped state.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueSubjectChangeSource
{
    /// <summary>Subscribes to current-subject or context invalidation.</summary>
    IDisposable OnChange(Action listener);
}
