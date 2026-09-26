namespace Configlue;

/// <summary>A state value and the revision on which the write is based.</summary>
public readonly record struct StateWriteRequest<T>(
    T Value,
    string? ExpectedRevision = null,
    bool CheckRevision = false
);
