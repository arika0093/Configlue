namespace Configlue;

/// <summary>The result of writing one source-local patch; composite writes identify physical component sources.</summary>
/// <remarks>Advanced vocabulary: exposes logical source and physical resource identities.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public readonly record struct StateSourceWriteResult
{
    /// <summary>Gets or initializes the logical source registration identifier, not a physical resource identity.</summary>
    public SourceId SourceId { get; init; }

    /// <summary>Gets or initializes the <see cref="ResourceId"/> value.</summary>
    public ResourceId? ResourceId { get; init; }

    /// <summary>Gets or initializes the <see cref="Revision"/> value.</summary>
    public string? Revision { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="SourceId">The initial value for the <see cref="SourceId"/> property.</param>
    /// <param name="ResourceId">The initial value for the <see cref="ResourceId"/> property.</param>
    /// <param name="Revision">The initial value for the <see cref="Revision"/> property.</param>
    public StateSourceWriteResult(SourceId SourceId, ResourceId? ResourceId, string? Revision)
    {
        this.SourceId = SourceId;
        this.ResourceId = ResourceId;
        this.Revision = Revision;
    }

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="SourceId">Receives the current <see cref="SourceId"/> value.</param>
    /// <param name="ResourceId">Receives the current <see cref="ResourceId"/> value.</param>
    /// <param name="Revision">Receives the current <see cref="Revision"/> value.</param>
    public void Deconstruct(out SourceId SourceId, out ResourceId? ResourceId, out string? Revision)
    {
        SourceId = this.SourceId;
        ResourceId = this.ResourceId;
        Revision = this.Revision;
    }
}

/// <summary>The complete logical source outcomes and physical write count of an application write.</summary>
/// <remarks>State-name identity and subject scope are reported separately from source and physical resource identity.
/// Advanced vocabulary: exposes logical source and physical resource identities.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class StateWriteReceipt
{
    /// <summary>An operation that performed no writes.</summary>
    public static StateWriteReceipt Empty { get; } = new([], 0);

    /// <summary>The revision when exactly one source was written; otherwise null.</summary>
    public string? Revision => Sources.Count == 1 ? Sources[0].Revision : null;

    /// <summary>Creates an application write receipt.</summary>
    public StateWriteReceipt(
        IEnumerable<StateSourceWriteResult> sources,
        int physicalWriteCount,
        string stateName = "",
        SubjectKey subjectKey = default
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (physicalWriteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalWriteCount));
        }

        var sourceResults = sources.ToArray();
        if (
            sourceResults.Any(static result => result.SourceId.IsDefault)
            || sourceResults.Select(static result => result.SourceId).Distinct().Count()
                != sourceResults.Length
        )
        {
            throw new ArgumentException(
                "Source write results must have unique, non-empty source IDs.",
                nameof(sources)
            );
        }

        if (physicalWriteCount > sourceResults.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(physicalWriteCount),
                "Physical writes cannot exceed logical source writes."
            );
        }

        Sources = Array.AsReadOnly(sourceResults);
        PhysicalWriteCount = physicalWriteCount;
        StateName = stateName ?? string.Empty;
        SubjectKey = subjectKey;
    }

    /// <summary>The result for each patched source.</summary>
    public IReadOnlyList<StateSourceWriteResult> Sources { get; }

    /// <summary>The number of physical resource writes performed.</summary>
    public int PhysicalWriteCount { get; }

    /// <summary>The state-name identity of the state instance that performed the write.</summary>
    public string StateName { get; }

    /// <summary>The subject scope of the write, or <see cref="SubjectKey.Default"/> for server-wide writes.</summary>
    public SubjectKey SubjectKey { get; }
}
