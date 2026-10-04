using System.Collections.ObjectModel;

namespace Configlue.State;

/// <summary>A backend revision associated with one logical source identifier.</summary>
/// <remarks>Advanced revision vocabulary.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public readonly record struct StateRevision
{
    /// <summary>Gets or initializes the <see cref="SourceId"/> value.</summary>
    public SourceId SourceId { get; init; }

    /// <summary>Gets or initializes the <see cref="Revision"/> value.</summary>
    public string? Revision { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="SourceId">The initial value for the <see cref="SourceId"/> property.</param>
    /// <param name="Revision">The initial value for the <see cref="Revision"/> property.</param>
    public StateRevision(SourceId SourceId, string? Revision)
    {
        this.SourceId = SourceId;
        this.Revision = Revision;
    }

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="SourceId">Receives the current <see cref="SourceId"/> value.</param>
    /// <param name="Revision">Receives the current <see cref="Revision"/> value.</param>
    public void Deconstruct(out SourceId SourceId, out string? Revision)
    {
        SourceId = this.SourceId;
        Revision = this.Revision;
    }
}

/// <summary>Direct and nested revisions observed during one state resolution.</summary>
/// <remarks>
/// <para>Advanced revision vocabulary.</para>
/// <para>
/// Revision maps are plain dictionaries materialized eagerly at construction
/// (issue #276). The earlier empty/single/small representations with lazy views
/// saved roughly one dictionary allocation on first/changed reads, but stable
/// reads reuse the previous resolution without constructing a vector at all,
/// so the saving applied only to infrequent changed reads while roughly
/// doubling the size of this type. Within the simple-settings budget
/// (<c>SingleFileSettingsBenchmarks</c>: warm read at most 3x the direct
/// deserialization baseline) one small Gen0 dictionary per changed read is
/// negligible next to file I/O and deserialization.
/// </para>
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed partial class StateRevisionVector
{
    private static readonly IReadOnlyDictionary<SourceId, string?> EmptyRevisions =
        new ReadOnlyDictionary<SourceId, string?>(new Dictionary<SourceId, string?>());
    private static readonly IReadOnlyDictionary<
        SourceId,
        StateRevisionVector
    > EmptyNestedRevisions = new ReadOnlyDictionary<SourceId, StateRevisionVector>(
        new Dictionary<SourceId, StateRevisionVector>()
    );

    private readonly IReadOnlyDictionary<SourceId, string?> _revisions;
    private readonly IReadOnlyDictionary<SourceId, StateRevisionVector> _nestedRevisions;

    /// <summary>Creates a revision vector from the participating sources.</summary>
    public StateRevisionVector(IEnumerable<StateRevision> revisions)
        : this(revisions, []) { }

    /// <summary>Creates a revision vector with nested resolution identity for logical sources.</summary>
    /// <param name="revisions">The revisions associated with sources at this level.</param>
    /// <param name="nestedRevisions">Child vectors keyed by the logical source that returned them.</param>
    public StateRevisionVector(
        IEnumerable<StateRevision> revisions,
        IEnumerable<KeyValuePair<SourceId, StateRevisionVector>> nestedRevisions
    )
    {
        ArgumentNullException.ThrowIfNull(revisions);
        ArgumentNullException.ThrowIfNull(nestedRevisions);
        _revisions = CreateRevisionMap(revisions);
        _nestedRevisions = CreateNestedRevisionMap(nestedRevisions);
    }

    /// <summary>Creates a revision vector from spans without requiring collection enumerators.</summary>
    public static StateRevisionVector FromSpan(
        ReadOnlySpan<StateRevision> revisions,
        ReadOnlySpan<KeyValuePair<SourceId, StateRevisionVector>> nestedRevisions = default
    ) => new(CreateRevisionMap(revisions), CreateNestedRevisionMap(nestedRevisions));

    /// <summary>Creates a revision vector for one source without allocating a temporary collection.</summary>
    public static StateRevisionVector FromSingle(
        StateRevision revision,
        StateRevisionVector? nestedRevision = null
    )
    {
        ValidateSourceId(revision.SourceId);
        var revisions = new ReadOnlyDictionary<SourceId, string?>(
            new Dictionary<SourceId, string?>(1) { [revision.SourceId] = revision.Revision }
        );
        IReadOnlyDictionary<SourceId, StateRevisionVector> nested = nestedRevision is null
            ? EmptyNestedRevisions
            : new ReadOnlyDictionary<SourceId, StateRevisionVector>(
                new Dictionary<SourceId, StateRevisionVector>(1)
                {
                    [revision.SourceId] = nestedRevision,
                }
            );
        return new StateRevisionVector(revisions, nested);
    }

    private StateRevisionVector(
        IReadOnlyDictionary<SourceId, string?> revisions,
        IReadOnlyDictionary<SourceId, StateRevisionVector> nestedRevisions
    )
    {
        _revisions = revisions;
        _nestedRevisions = nestedRevisions;
    }

    private static IReadOnlyDictionary<SourceId, string?> CreateRevisionMap(
        IEnumerable<StateRevision> revisions
    )
    {
        using var enumerator = revisions.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return EmptyRevisions;
        }

        var first = enumerator.Current;
        ValidateSourceId(first.SourceId);
        var values = new Dictionary<SourceId, string?> { [first.SourceId] = first.Revision };
        while (enumerator.MoveNext())
        {
            var item = enumerator.Current;
            ValidateSourceId(item.SourceId);
            if (!values.TryAdd(item.SourceId, item.Revision))
            {
                throw new ArgumentException(
                    $"Source '{item.SourceId}' occurs more than once in the revision vector.",
                    nameof(revisions)
                );
            }
        }

        return new ReadOnlyDictionary<SourceId, string?>(values);
    }

    private static IReadOnlyDictionary<SourceId, string?> CreateRevisionMap(
        ReadOnlySpan<StateRevision> revisions
    )
    {
        if (revisions.IsEmpty)
        {
            return EmptyRevisions;
        }

        var values = new Dictionary<SourceId, string?>(revisions.Length);
        foreach (var item in revisions)
        {
            ValidateSourceId(item.SourceId);
            if (!values.TryAdd(item.SourceId, item.Revision))
            {
                throw new ArgumentException(
                    $"Source '{item.SourceId}' occurs more than once in the revision vector.",
                    nameof(revisions)
                );
            }
        }

        return new ReadOnlyDictionary<SourceId, string?>(values);
    }

    private static IReadOnlyDictionary<SourceId, StateRevisionVector> CreateNestedRevisionMap(
        IEnumerable<KeyValuePair<SourceId, StateRevisionVector>> nestedRevisions
    )
    {
        using var enumerator = nestedRevisions.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return EmptyNestedRevisions;
        }

        var first = enumerator.Current;
        ValidateSourceId(first.Key);
        ArgumentNullException.ThrowIfNull(first.Value);
        var values = new Dictionary<SourceId, StateRevisionVector> { [first.Key] = first.Value };
        while (enumerator.MoveNext())
        {
            var item = enumerator.Current;
            ValidateSourceId(item.Key);
            ArgumentNullException.ThrowIfNull(item.Value);
            if (!values.TryAdd(item.Key, item.Value))
            {
                throw new ArgumentException(
                    $"Source '{item.Key}' occurs more than once in the nested revision vectors.",
                    nameof(nestedRevisions)
                );
            }
        }

        return new ReadOnlyDictionary<SourceId, StateRevisionVector>(values);
    }

    private static IReadOnlyDictionary<SourceId, StateRevisionVector> CreateNestedRevisionMap(
        ReadOnlySpan<KeyValuePair<SourceId, StateRevisionVector>> nestedRevisions
    )
    {
        if (nestedRevisions.IsEmpty)
        {
            return EmptyNestedRevisions;
        }

        var values = new Dictionary<SourceId, StateRevisionVector>(nestedRevisions.Length);
        foreach (var item in nestedRevisions)
        {
            ValidateSourceId(item.Key);
            ArgumentNullException.ThrowIfNull(item.Value);
            if (!values.TryAdd(item.Key, item.Value))
            {
                throw new ArgumentException(
                    $"Source '{item.Key}' occurs more than once in the nested revision vectors.",
                    nameof(nestedRevisions)
                );
            }
        }

        return new ReadOnlyDictionary<SourceId, StateRevisionVector>(values);
    }

    private static void ValidateSourceId(SourceId sourceId)
    {
        if (sourceId.IsDefault)
        {
            throw new ArgumentException("Source IDs must not be default values.", nameof(sourceId));
        }
    }

    /// <summary>Direct revisions captured by the most recent source resolution.</summary>
    public IReadOnlyDictionary<SourceId, string?> Revisions => _revisions;

    /// <summary>
    /// Nested resolution vectors returned by logical sources, keyed by the outer source identifier. These
    /// preserve composite-source identity separately from direct revisions used for write concurrency.
    /// </summary>
    public IReadOnlyDictionary<SourceId, StateRevisionVector> NestedRevisions => _nestedRevisions;

    /// <summary>Gets whether a source participated in the resolution and its revision, which may be null.</summary>
    public bool TryGetRevision(SourceId sourceId, out string? revision) =>
        _revisions.TryGetValue(sourceId, out revision);

    /// <summary>Checks that all direct revision sources remain active; nested sources are independent.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Major Code Smell",
        "S3267",
        Justification = "Direct loops avoid LINQ closures and enumerator allocations in watcher membership checks."
    )]
    internal bool ContainsOnlySources(HashSet<SourceId> activeSourceIds)
    {
        var revisions = _revisions;
        // Inverting the lookup avoids boxing a dictionary enumerator. Active source
        // sets from the runtime use the same default identity comparer as revisions.
        // Preserve ordinary set membership if a caller supplies a custom comparer.
        if (!ReferenceEquals(activeSourceIds.Comparer, EqualityComparer<SourceId>.Default))
        {
            foreach (var sourceId in revisions.Keys)
            {
                if (!activeSourceIds.Contains(sourceId))
                    return false;
            }
            return true;
        }

        var remaining = revisions.Count;
        if (remaining == 0)
            return true;
        if (remaining > activeSourceIds.Count)
            return false;
        foreach (var sourceId in activeSourceIds)
        {
            if (revisions.ContainsKey(sourceId) && --remaining == 0)
                return true;
        }
        return false;
    }

    /// <summary>Gets the nested revision vector for a logical source.</summary>
    public bool TryGetNestedRevisions(SourceId sourceId, out StateRevisionVector? revisions) =>
        _nestedRevisions.TryGetValue(sourceId, out revisions);
}
