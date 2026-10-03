using System.Collections;
using System.Collections.ObjectModel;

namespace Configlue.State;

/// <summary>A backend revision associated with one logical source identifier.</summary>
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
public sealed class StateRevisionVector
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
    )
    {
        if (revisions.Length <= 1 && nestedRevisions.Length <= 1)
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

        return new StateRevisionVector(
            CreateRevisionMapFromSpan(revisions),
            CreateNestedRevisionMapFromSpan(nestedRevisions)
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
        StateRevisionVector? singleNestedRevision
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
        if (!enumerator.MoveNext())
        {
            return new SingleEntryReadOnlyDictionary<string?>(first.SourceId, first.Revision);
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
        if (!enumerator.MoveNext())
        {
            return new SingleEntryReadOnlyDictionary<StateRevisionVector>(first.Key, first.Value);
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
    public IReadOnlyDictionary<SourceId, string?> Revisions
    {
        get
        {
            var revisions = _revisions;
            if (revisions is not null)
            {
                return revisions;
            }

            revisions = new SingleEntryReadOnlyDictionary<string?>(
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

            revisions = new SingleEntryReadOnlyDictionary<StateRevisionVector>(
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
            revision = _singleRevision;
            return sourceId == _singleRevisionSource;
        }

        return (_revisions ?? EmptyRevisions).TryGetValue(sourceId, out revision);
    }

    /// <summary>Gets the nested revision vector for a logical source.</summary>
    public bool TryGetNestedRevisions(SourceId sourceId, out StateRevisionVector? revisions)
    {
        if (_hasSingleNestedRevision)
        {
            revisions = _singleNestedRevision;
            return sourceId == _singleNestedRevisionSource;
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
