namespace Configlue;

/// <summary>Optionally reports when a scoped accessor should resolve its current subject again.</summary>
public interface IConfiglueSubjectChangeSource
{
    /// <summary>Subscribes to current-subject or context invalidation.</summary>
    IDisposable OnChange(Action listener);
}
