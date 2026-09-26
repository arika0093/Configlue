using System.Collections.ObjectModel;

namespace Configlue;

/// <summary>A backend revision associated with one logical source identifier.</summary>
public readonly record struct StateRevision(string SourceId, string? Revision);

/// <summary>Direct and nested revisions observed during one state resolution.</summary>
public sealed class StateRevisionVector
{
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

        var nestedValues = new Dictionary<string, StateRevisionVector>(StringComparer.Ordinal);
        foreach (var item in nestedRevisions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Key);
            ArgumentNullException.ThrowIfNull(item.Value);
            if (!nestedValues.TryAdd(item.Key, item.Value))
            {
                throw new ArgumentException(
                    $"Source '{item.Key}' occurs more than once in the nested revision vectors.",
                    nameof(nestedRevisions)
                );
            }
        }

        _revisions = new ReadOnlyDictionary<string, string?>(values);
        _nestedRevisions = new ReadOnlyDictionary<string, StateRevisionVector>(nestedValues);
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
