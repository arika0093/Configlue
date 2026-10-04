using System.Collections;
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
/// <remarks>Advanced revision vocabulary.</remarks>
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

    private const int SmallDictionaryThreshold = 4;

    private IReadOnlyDictionary<SourceId, string?>? _revisions;
    private IReadOnlyDictionary<SourceId, StateRevisionVector>? _nestedRevisions;
    private readonly bool _hasSingleRevision;
    private readonly SourceId _singleRevisionSource;
    private readonly string? _singleRevision;
    private readonly bool _hasSingleNestedRevision;
    private readonly SourceId _singleNestedRevisionSource;
    private readonly StateRevisionVector? _singleNestedRevision;
    private readonly KeyValuePair<SourceId, string?>[]? _smallRevisions;
    private readonly KeyValuePair<SourceId, StateRevisionVector>[]? _smallNestedRevisions;

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
        var revisionData = CreateRevisionMapData(revisions);
        var nestedRevisionData = CreateNestedRevisionMapData(nestedRevisions);
        _revisions = revisionData.Map;
        _nestedRevisions = nestedRevisionData.Map;
        _hasSingleRevision = revisionData.HasSingle;
        _singleRevisionSource = revisionData.SourceId;
        _singleRevision = revisionData.Revision;
        _hasSingleNestedRevision = nestedRevisionData.HasSingle;
        _singleNestedRevisionSource = nestedRevisionData.SourceId;
        _singleNestedRevision = nestedRevisionData.Revision;
    }

    /// <summary>Creates a revision vector from spans without requiring collection enumerators.</summary>
    public static StateRevisionVector FromSpan(
        ReadOnlySpan<StateRevision> revisions,
        ReadOnlySpan<KeyValuePair<SourceId, StateRevisionVector>> nestedRevisions = default
    )
    {
        if (
            revisions.Length <= SmallDictionaryThreshold
            && nestedRevisions.Length <= SmallDictionaryThreshold
        )
        {
            var hasRevision = !revisions.IsEmpty;
            var singleRevision = hasRevision ? revisions[0] : default;
            if (hasRevision)
            {
                ValidateSourceId(singleRevision.SourceId);
            }

            var hasNestedRevision = !nestedRevisions.IsEmpty;
            var singleNestedRevision = hasNestedRevision ? nestedRevisions[0] : default;
            if (hasNestedRevision)
            {
                ValidateSourceId(singleNestedRevision.Key);
                ArgumentNullException.ThrowIfNull(singleNestedRevision.Value);
            }

            if (revisions.Length <= 1 && nestedRevisions.Length <= 1)
            {
                return new StateRevisionVector(
                    hasRevision ? null : EmptyRevisions,
                    hasNestedRevision ? null : EmptyNestedRevisions,
                    hasRevision,
                    singleRevision.SourceId,
                    singleRevision.Revision,
                    hasNestedRevision,
                    singleNestedRevision.Key,
                    singleNestedRevision.Value
                );
            }

            var revisionEntries = CreateRevisionEntriesFromSpan(revisions);
            var nestedEntries = CreateNestedRevisionEntriesFromSpan(nestedRevisions);
            var hasSingleRevision = hasRevision && revisions.Length == 1;
            var hasSingleNestedRevision = hasNestedRevision && nestedRevisions.Length == 1;
            IReadOnlyDictionary<SourceId, string?>? revisionMap = null;
            if (!hasSingleRevision && revisionEntries is null)
            {
                revisionMap = EmptyRevisions;
            }

            IReadOnlyDictionary<SourceId, StateRevisionVector>? nestedRevisionMap = null;
            if (!hasSingleNestedRevision && nestedEntries is null)
            {
                nestedRevisionMap = EmptyNestedRevisions;
            }

            return new StateRevisionVector(
                revisionMap,
                nestedRevisionMap,
                hasSingleRevision,
                singleRevision.SourceId,
                singleRevision.Revision,
                hasSingleNestedRevision,
                singleNestedRevision.Key,
                singleNestedRevision.Value,
                revisionEntries,
                nestedEntries
            );
        }

        return new StateRevisionVector(
            CreateRevisionMapFromSpan(revisions),
            CreateNestedRevisionMapFromSpan(nestedRevisions)
        );
    }

    /// <summary>Creates a revision vector for one source without allocating a temporary collection.</summary>
    public static StateRevisionVector FromSingle(
        StateRevision revision,
        StateRevisionVector? nestedRevision = null
    )
    {
        ValidateSourceId(revision.SourceId);
        return new StateRevisionVector(
            null,
            nestedRevision is null ? EmptyNestedRevisions : null,
            true,
            revision.SourceId,
            revision.Revision,
            nestedRevision is not null,
            revision.SourceId,
            nestedRevision
        );
    }

    private StateRevisionVector(
        IReadOnlyDictionary<SourceId, string?> revisions,
        IReadOnlyDictionary<SourceId, StateRevisionVector> nestedRevisions
    )
    {
        _revisions = revisions;
        _nestedRevisions = nestedRevisions;
    }

    private StateRevisionVector(
        IReadOnlyDictionary<SourceId, string?>? revisions,
        IReadOnlyDictionary<SourceId, StateRevisionVector>? nestedRevisions,
        bool hasSingleRevision,
        SourceId singleRevisionSource,
        string? singleRevision,
        bool hasSingleNestedRevision,
        SourceId singleNestedRevisionSource,
        StateRevisionVector? singleNestedRevision,
        KeyValuePair<SourceId, string?>[]? smallRevisions = null,
        KeyValuePair<SourceId, StateRevisionVector>[]? smallNestedRevisions = null
    )
    {
        _revisions = revisions;
        _nestedRevisions = nestedRevisions;
        _hasSingleRevision = hasSingleRevision;
        _singleRevisionSource = singleRevisionSource;
        _singleRevision = singleRevision;
        _hasSingleNestedRevision = hasSingleNestedRevision;
        _singleNestedRevisionSource = singleNestedRevisionSource;
        _singleNestedRevision = singleNestedRevision;
        _smallRevisions = smallRevisions;
        _smallNestedRevisions = smallNestedRevisions;
    }

    private static KeyValuePair<SourceId, string?>[]? CreateRevisionEntriesFromSpan(
        ReadOnlySpan<StateRevision> revisions
    )
    {
        if (revisions.IsEmpty || revisions.Length == 1)
        {
            return null;
        }

        var entries = new KeyValuePair<SourceId, string?>[revisions.Length];
        for (var index = 0; index < revisions.Length; index++)
        {
            var item = revisions[index];
            ValidateSourceId(item.SourceId);
            for (var previous = 0; previous < index; previous++)
            {
                if (entries[previous].Key == item.SourceId)
                {
                    throw new ArgumentException(
                        $"Source '{item.SourceId}' occurs more than once in the revision vector.",
                        nameof(revisions)
                    );
                }
            }

            entries[index] = new KeyValuePair<SourceId, string?>(item.SourceId, item.Revision);
        }

        return entries;
    }

    private static KeyValuePair<
        SourceId,
        StateRevisionVector
    >[]? CreateNestedRevisionEntriesFromSpan(
        ReadOnlySpan<KeyValuePair<SourceId, StateRevisionVector>> revisions
    )
    {
        if (revisions.IsEmpty || revisions.Length == 1)
        {
            return null;
        }

        var entries = new KeyValuePair<SourceId, StateRevisionVector>[revisions.Length];
        for (var index = 0; index < revisions.Length; index++)
        {
            var item = revisions[index];
            ValidateSourceId(item.Key);
            ArgumentNullException.ThrowIfNull(item.Value);
            for (var previous = 0; previous < index; previous++)
            {
                if (entries[previous].Key == item.Key)
                {
                    throw new ArgumentException(
                        $"Source '{item.Key}' occurs more than once in the nested revision vectors.",
                        nameof(revisions)
                    );
                }
            }

            entries[index] = item;
        }

        return entries;
    }

    private static IReadOnlyDictionary<SourceId, string?> CreateRevisionMapFromSpan(
        ReadOnlySpan<StateRevision> revisions
    )
    {
        if (revisions.IsEmpty)
        {
            return EmptyRevisions;
        }

        var first = revisions[0];
        ValidateSourceId(first.SourceId);
        if (revisions.Length == 1)
        {
            return new SingleEntryReadOnlyDictionary<string?>(first.SourceId, first.Revision);
        }

        if (revisions.Length <= SmallDictionaryThreshold)
        {
            var entries = new KeyValuePair<SourceId, string?>[revisions.Length];
            entries[0] = new KeyValuePair<SourceId, string?>(first.SourceId, first.Revision);
            for (var index = 1; index < revisions.Length; index++)
            {
                var item = revisions[index];
                ValidateSourceId(item.SourceId);
                for (var existing = 0; existing < index; existing++)
                {
                    if (entries[existing].Key == item.SourceId)
                    {
                        throw new ArgumentException(
                            $"Source '{item.SourceId}' occurs more than once in the revision vector.",
                            nameof(revisions)
                        );
                    }
                }

                entries[index] = new KeyValuePair<SourceId, string?>(item.SourceId, item.Revision);
            }

            return new SmallReadOnlyDictionary<string?>(entries);
        }

        var values = new Dictionary<SourceId, string?>(revisions.Length)
        {
            [first.SourceId] = first.Revision,
        };
        for (var index = 1; index < revisions.Length; index++)
        {
            var item = revisions[index];
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

    private static IReadOnlyDictionary<
        SourceId,
        StateRevisionVector
    > CreateNestedRevisionMapFromSpan(
        ReadOnlySpan<KeyValuePair<SourceId, StateRevisionVector>> nestedRevisions
    )
    {
        if (nestedRevisions.IsEmpty)
        {
            return EmptyNestedRevisions;
        }

        var first = nestedRevisions[0];
        ValidateSourceId(first.Key);
        ArgumentNullException.ThrowIfNull(first.Value);
        if (nestedRevisions.Length == 1)
        {
            return new SingleEntryReadOnlyDictionary<StateRevisionVector>(first.Key, first.Value);
        }

        if (nestedRevisions.Length <= SmallDictionaryThreshold)
        {
            var entries = new KeyValuePair<SourceId, StateRevisionVector>[nestedRevisions.Length];
            entries[0] = new KeyValuePair<SourceId, StateRevisionVector>(first.Key, first.Value);
            for (var index = 1; index < nestedRevisions.Length; index++)
            {
                var item = nestedRevisions[index];
                ValidateSourceId(item.Key);
                ArgumentNullException.ThrowIfNull(item.Value);
                for (var existing = 0; existing < index; existing++)
                {
                    if (entries[existing].Key == item.Key)
                    {
                        throw new ArgumentException(
                            $"Source '{item.Key}' occurs more than once in the nested revision vectors.",
                            nameof(nestedRevisions)
                        );
                    }
                }

                entries[index] = new KeyValuePair<SourceId, StateRevisionVector>(
                    item.Key,
                    item.Value
                );
            }

            return new SmallReadOnlyDictionary<StateRevisionVector>(entries);
        }

        var values = new Dictionary<SourceId, StateRevisionVector>(nestedRevisions.Length)
        {
            [first.Key] = first.Value,
        };
        for (var index = 1; index < nestedRevisions.Length; index++)
        {
            var item = nestedRevisions[index];
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

    private static (
        IReadOnlyDictionary<SourceId, string?>? Map,
        bool HasSingle,
        SourceId SourceId,
        string? Revision
    ) CreateRevisionMapData(IEnumerable<StateRevision> revisions)
    {
        using var enumerator = revisions.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return (EmptyRevisions, false, default, null);
        }

        var first = enumerator.Current;
        ValidateSourceId(first.SourceId);
        if (!enumerator.MoveNext())
        {
            return (null, true, first.SourceId, first.Revision);
        }

        var values = new Dictionary<SourceId, string?>();
        values.Add(first.SourceId, first.Revision);
        do
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
        } while (enumerator.MoveNext());

        return (new ReadOnlyDictionary<SourceId, string?>(values), false, default, null);
    }

    private static (
        IReadOnlyDictionary<SourceId, StateRevisionVector>? Map,
        bool HasSingle,
        SourceId SourceId,
        StateRevisionVector? Revision
    ) CreateNestedRevisionMapData(
        IEnumerable<KeyValuePair<SourceId, StateRevisionVector>> nestedRevisions
    )
    {
        using var enumerator = nestedRevisions.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return (EmptyNestedRevisions, false, default, null);
        }

        var first = enumerator.Current;
        ValidateSourceId(first.Key);
        ArgumentNullException.ThrowIfNull(first.Value);
        if (!enumerator.MoveNext())
        {
            return (null, true, first.Key, first.Value);
        }

        var values = new Dictionary<SourceId, StateRevisionVector>() { [first.Key] = first.Value };
        do
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
        } while (enumerator.MoveNext());

        return (
            new ReadOnlyDictionary<SourceId, StateRevisionVector>(values),
            false,
            default,
            null
        );
    }

    private static void ValidateSourceId(SourceId sourceId)
    {
        if (sourceId.IsDefault)
        {
            throw new ArgumentException("Source IDs must not be default values.", nameof(sourceId));
        }
    }

    /// <summary>Direct revisions captured by the most recent source resolution.</summary>
    public IReadOnlyDictionary<SourceId, string?> Revisions
    {
        get
        {
            var revisions = _revisions;
            if (revisions is not null)
            {
                return revisions;
            }

            revisions = _smallRevisions is { } entries
                ? new SmallReadOnlyDictionary<string?>(entries)
                : new SingleEntryReadOnlyDictionary<string?>(
                    _singleRevisionSource,
                    _singleRevision
                );
            return Interlocked.CompareExchange(ref _revisions, revisions, null) ?? revisions;
        }
    }

    /// <summary>
    /// Nested resolution vectors returned by logical sources, keyed by the outer source identifier. These
    /// preserve composite-source identity separately from direct revisions used for write concurrency.
    /// </summary>
    public IReadOnlyDictionary<SourceId, StateRevisionVector> NestedRevisions
    {
        get
        {
            var revisions = _nestedRevisions;
            if (revisions is not null)
            {
                return revisions;
            }

            revisions = _smallNestedRevisions is { } entries
                ? new SmallReadOnlyDictionary<StateRevisionVector>(entries)
                : new SingleEntryReadOnlyDictionary<StateRevisionVector>(
                    _singleNestedRevisionSource,
                    _singleNestedRevision!
                );
            return Interlocked.CompareExchange(ref _nestedRevisions, revisions, null) ?? revisions;
        }
    }

    /// <summary>Gets whether a source participated in the resolution and its revision, which may be null.</summary>
    public bool TryGetRevision(SourceId sourceId, out string? revision)
    {
        if (_hasSingleRevision)
        {
            if (sourceId == _singleRevisionSource)
            {
                revision = _singleRevision;
                return true;
            }

            revision = default;
            return false;
        }

        if (_smallRevisions is { } entries)
        {
            for (var index = 0; index < entries.Length; index++)
            {
                if (entries[index].Key == sourceId)
                {
                    revision = entries[index].Value;
                    return true;
                }
            }

            revision = default;
            return false;
        }

        return (_revisions ?? EmptyRevisions).TryGetValue(sourceId, out revision);
    }

    /// <summary>Checks that all direct revision sources remain active; nested sources are independent.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Major Code Smell",
        "S3267",
        Justification = "Direct array/set loops avoid LINQ closures and enumerator allocations in watcher membership checks."
    )]
    internal bool ContainsOnlySources(HashSet<SourceId> activeSourceIds)
    {
        if (_hasSingleRevision)
        {
            return activeSourceIds.Contains(_singleRevisionSource);
        }
        if (_smallRevisions is { } entries)
        {
            foreach (var entry in entries)
            {
                if (!activeSourceIds.Contains(entry.Key))
                    return false;
            }
            return true;
        }

        var revisions = _revisions ?? EmptyRevisions;
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
    public bool TryGetNestedRevisions(SourceId sourceId, out StateRevisionVector? revisions)
    {
        if (_hasSingleNestedRevision)
        {
            if (sourceId == _singleNestedRevisionSource)
            {
                revisions = _singleNestedRevision;
                return true;
            }

            revisions = null;
            return false;
        }

        if (_smallNestedRevisions is { } entries)
        {
            for (var index = 0; index < entries.Length; index++)
            {
                if (entries[index].Key == sourceId)
                {
                    revisions = entries[index].Value;
                    return true;
                }
            }

            revisions = null;
            return false;
        }

        return (_nestedRevisions ?? EmptyNestedRevisions).TryGetValue(sourceId, out revisions);
    }

    private sealed class SingleEntryReadOnlyDictionary<TValue>(SourceId key, TValue storedValue)
        : IReadOnlyDictionary<SourceId, TValue>
    {
        public TValue this[SourceId key] =>
            TryGetValue(key, out var entry)
                ? entry
                : throw new KeyNotFoundException($"Key '{key}' was not present in the dictionary.");

        public IEnumerable<SourceId> Keys => EnumerateKeys();

        public IEnumerable<TValue> Values => EnumerateValues();

        public int Count => 1;

        public bool ContainsKey(SourceId candidate)
        {
            return candidate == key;
        }

        public bool TryGetValue(SourceId candidate, out TValue value)
        {
            if (candidate == key)
            {
                value = storedValue;
                return true;
            }

            value = default!;
            return false;
        }

        public IEnumerator<KeyValuePair<SourceId, TValue>> GetEnumerator()
        {
            yield return new KeyValuePair<SourceId, TValue>(key, storedValue);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private IEnumerable<SourceId> EnumerateKeys()
        {
            yield return key;
        }

        private IEnumerable<TValue> EnumerateValues()
        {
            yield return storedValue;
        }
    }

    private sealed class SmallReadOnlyDictionary<TValue>(KeyValuePair<SourceId, TValue>[] entries)
        : IReadOnlyDictionary<SourceId, TValue>
    {
        public TValue this[SourceId key] =>
            TryGetValue(key, out var entry)
                ? entry
                : throw new KeyNotFoundException($"Key '{key}' was not present in the dictionary.");

        public IEnumerable<SourceId> Keys
        {
            get
            {
                foreach (var entry in entries)
                {
                    yield return entry.Key;
                }
            }
        }

        public IEnumerable<TValue> Values
        {
            get
            {
                foreach (var entry in entries)
                {
                    yield return entry.Value;
                }
            }
        }

        public int Count => entries.Length;

        public bool ContainsKey(SourceId key)
        {
            for (var index = 0; index < entries.Length; index++)
            {
                if (entries[index].Key == key)
                {
                    return true;
                }
            }

            return false;
        }

        public bool TryGetValue(SourceId key, out TValue value)
        {
            for (var index = 0; index < entries.Length; index++)
            {
                if (entries[index].Key == key)
                {
                    value = entries[index].Value;
                    return true;
                }
            }

            value = default!;
            return false;
        }

        public IEnumerator<KeyValuePair<SourceId, TValue>> GetEnumerator() =>
            ((IEnumerable<KeyValuePair<SourceId, TValue>>)entries).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => entries.GetEnumerator();
    }
}
