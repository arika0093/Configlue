using System.Collections.ObjectModel;

namespace Configlue;

/// <summary>A backend revision associated with one logical source identifier.</summary>
public readonly record struct StateRevision(string SourceId, string? Revision);

/// <summary>The revision observed for each source participating in one resolution attempt.</summary>
public sealed class StateRevisionVector
{
    private readonly IReadOnlyDictionary<string, string?> _revisions;

    /// <summary>Creates a revision vector from the participating sources.</summary>
    public StateRevisionVector(IEnumerable<StateRevision> revisions)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var item in revisions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.SourceId);
            if (!values.TryAdd(item.SourceId, item.Revision))
            {
                throw new ArgumentException($"Source '{item.SourceId}' occurs more than once in the revision vector.", nameof(revisions));
            }
        }

        _revisions = new ReadOnlyDictionary<string, string?>(values);
    }

    /// <summary>Revisions captured by the most recent source resolution.</summary>
    public IReadOnlyDictionary<string, string?> Revisions => _revisions;

    /// <summary>Gets whether a source participated in the resolution and its revision, which may be null.</summary>
    public bool TryGetRevision(string sourceId, out string? revision) => _revisions.TryGetValue(sourceId, out revision);
}
