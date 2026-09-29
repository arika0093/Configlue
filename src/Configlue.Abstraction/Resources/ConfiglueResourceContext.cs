namespace Configlue.Resources;

/// <summary>Identifies the logical subject and source-specific key for a resource operation.</summary>
public readonly record struct ConfiglueResourceContext(IConfiglueSubject Subject, SubjectKey Key);
