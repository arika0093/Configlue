using System.Collections.ObjectModel;

namespace Configlue;

/// <summary>A backend revision associated with one logical source identifier.</summary>
public readonly record struct StateRevision
{
    /// <summary>Gets or initializes the <see cref="SourceId"/> value.</summary>
    public string SourceId { get; init; }

    /// <summary>Gets or initializes the <see cref="Revision"/> value.</summary>
    public string? Revision { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="SourceId">The initial value for the <see cref="SourceId"/> property.</param>
    /// <param name="Revision">The initial value for the <see cref="Revision"/> property.</param>
    public StateRevision(string SourceId, string? Revision)
    {
        this.SourceId = SourceId;
        this.Revision = Revision;
    }

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="SourceId">Receives the current <see cref="SourceId"/> value.</param>
    /// <param name="Revision">Receives the current <see cref="Revision"/> value.</param>
    public void Deconstruct(out string SourceId, out string? Revision)
    {
        SourceId = this.SourceId;
        Revision = this.Revision;
    }
}

/// <summary>Direct and nested revisions observed during one state resolution.</summary>
public sealed class StateRevisionVector
{
    private static readonly IReadOnlyDictionary<string, StateRevisionVector> EmptyNestedRevisions =
        new ReadOnlyDictionary<string, StateRevisionVector>(
            new Dictionary<string, StateRevisionVector>(StringComparer.Ordinal)
        );

    private readonly IReadOnlyDictionary<string, string?> _revisions;
    private readonly IReadOnlyDictionary<string, StateRevisionVector> _nestedRevisions;

    /// <summary>Creates a revision vector from the participating sources.</summary>
    public StateRevisionVector(IEnumerable<StateRevision> revisions)
        : this(revisions, []) { }

    /// <summary>Creates a revision vector with nested resolution identity for logical sources.</summary>
    /// <param name="revisions">The revisions associated with sources at this level.</param>
    /// <param name="nestedRevisions">Child vectors keyed by the logical source that returned them.</param>
    public StateRevisionVector(
        IEnumerable<StateRevision> revisions,
        IEnumerable<KeyValuePair<string, StateRevisionVector>> nestedRevisions
    )
    {
        ArgumentNullException.ThrowIfNull(revisions);
        ArgumentNullException.ThrowIfNull(nestedRevisions);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var item in revisions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.SourceId);
            if (!values.TryAdd(item.SourceId, item.Revision))
            {
                throw new ArgumentException(
                    $"Source '{item.SourceId}' occurs more than once in the revision vector.",
                    nameof(revisions)
                );
            }
        }

        Dictionary<string, StateRevisionVector>? nestedValues = null;
        foreach (var item in nestedRevisions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Key);
            ArgumentNullException.ThrowIfNull(item.Value);
            nestedValues ??= new Dictionary<string, StateRevisionVector>(StringComparer.Ordinal);
            if (!nestedValues.TryAdd(item.Key, item.Value))
            {
                throw new ArgumentException(
                    $"Source '{item.Key}' occurs more than once in the nested revision vectors.",
                    nameof(nestedRevisions)
                );
            }
        }

        _revisions = new ReadOnlyDictionary<string, string?>(values);
        _nestedRevisions = nestedValues is null
            ? EmptyNestedRevisions
            : new ReadOnlyDictionary<string, StateRevisionVector>(nestedValues);
    }

    /// <summary>Direct revisions captured by the most recent source resolution.</summary>
    public IReadOnlyDictionary<string, string?> Revisions => _revisions;

    /// <summary>
    /// Nested resolution vectors returned by logical sources, keyed by the outer source identifier. These
    /// preserve composite-source identity separately from direct revisions used for write concurrency.
    /// </summary>
    public IReadOnlyDictionary<string, StateRevisionVector> NestedRevisions => _nestedRevisions;

    /// <summary>Gets whether a source participated in the resolution and its revision, which may be null.</summary>
    public bool TryGetRevision(string sourceId, out string? revision) =>
        _revisions.TryGetValue(sourceId, out revision);

    /// <summary>Gets the nested revision vector for a logical source.</summary>
    public bool TryGetNestedRevisions(string sourceId, out StateRevisionVector? revisions) =>
        _nestedRevisions.TryGetValue(sourceId, out revisions);
}
