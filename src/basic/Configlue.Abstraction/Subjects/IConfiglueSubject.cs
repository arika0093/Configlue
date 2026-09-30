namespace Configlue;

/// <summary>Identifies an application-defined scope for configuration state.</summary>
/// <remarks>Configlue uses only <see cref="Key"/> for logical identity.</remarks>
public interface IConfiglueSubject
{
    /// <summary>The canonical logical key for this subject.</summary>
    SubjectKey Key { get; }
}
