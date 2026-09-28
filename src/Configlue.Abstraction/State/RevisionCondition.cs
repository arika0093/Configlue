namespace Configlue.State;

/// <summary>An explicit optimistic concurrency precondition for a write.</summary>
public readonly record struct RevisionCondition
{
    private readonly byte _kind;

    private RevisionCondition(byte kind, string? revision)
    {
        _kind = kind;
        Revision = revision;
    }

    /// <summary>Writes without checking the previous revision.</summary>
    public static RevisionCondition None => default;

    /// <summary>Requires that no state or resource currently exists.</summary>
    public static RevisionCondition MustNotExist => new(1, null);

    /// <summary>The revision required by a matching condition; otherwise null.</summary>
    public string? Revision { get; }

    /// <summary>Whether this condition checks an existing revision.</summary>
    public bool IsMatch => _kind == 2;

    /// <summary>Whether this condition requires absence.</summary>
    public bool IsMustNotExist => _kind == 1;

    /// <summary>Whether this condition permits an unchecked write.</summary>
    public bool IsNone => _kind == 0;

    /// <summary>Requires an exact, ordinal match of the supplied revision.</summary>
    public static RevisionCondition Match(string revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return new(2, revision);
    }

    /// <summary>Requires the observed revision, or absence when the observation has no revision.</summary>
    public static RevisionCondition FromRevision(string? revision) =>
        revision is null ? MustNotExist : Match(revision);

    /// <summary>Tests this condition against the current revision and existence of the value.</summary>
    public bool IsSatisfiedBy(string? revision, bool exists) =>
        IsNone
        || (IsMustNotExist ? !exists : string.Equals(Revision, revision, StringComparison.Ordinal));
}
