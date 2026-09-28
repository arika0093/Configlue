using System.Collections;
using System.Collections.ObjectModel;

namespace Configlue.State;

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
    private static readonly IReadOnlyDictionary<string, string?> EmptyRevisions =
        new ReadOnlyDictionary<string, string?>(
            new Dictionary<string, string?>(StringComparer.Ordinal)
        );
    private static readonly IReadOnlyDictionary<string, StateRevisionVector> EmptyNestedRevisions =
        new ReadOnlyDictionary<string, StateRevisionVector>(
            new Dictionary<string, StateRevisionVector>(StringComparer.Ordinal)
        );

    private const int SmallDictionaryThreshold = 4;

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
        _revisions = CreateRevisionMap(revisions);
        _nestedRevisions = CreateNestedRevisionMap(nestedRevisions);
    }

    /// <summary>Creates a revision vector from spans without requiring collection enumerators.</summary>
    public static StateRevisionVector FromSpan(
        ReadOnlySpan<StateRevision> revisions,
        ReadOnlySpan<KeyValuePair<string, StateRevisionVector>> nestedRevisions = default
    ) =>
        new(CreateRevisionMapFromSpan(revisions), CreateNestedRevisionMapFromSpan(nestedRevisions));

    private StateRevisionVector(
        IReadOnlyDictionary<string, string?> revisions,
        IReadOnlyDictionary<string, StateRevisionVector> nestedRevisions
    )
    {
        _revisions = revisions;
        _nestedRevisions = nestedRevisions;
    }

    private static IReadOnlyDictionary<string, string?> CreateRevisionMapFromSpan(
        ReadOnlySpan<StateRevision> revisions
    )
    {
        if (revisions.IsEmpty)
        {
            return EmptyRevisions;
        }

        var first = revisions[0];
        ArgumentException.ThrowIfNullOrWhiteSpace(first.SourceId);
        if (revisions.Length == 1)
        {
            return new SingleEntryReadOnlyDictionary<string?>(first.SourceId, first.Revision);
        }

        if (revisions.Length <= SmallDictionaryThreshold)
        {
            var entries = new KeyValuePair<string, string?>[revisions.Length];
            entries[0] = new KeyValuePair<string, string?>(first.SourceId, first.Revision);
            for (var index = 1; index < revisions.Length; index++)
            {
                var item = revisions[index];
                ArgumentException.ThrowIfNullOrWhiteSpace(item.SourceId);
                for (var existing = 0; existing < index; existing++)
                {
                    if (StringComparer.Ordinal.Equals(entries[existing].Key, item.SourceId))
                    {
                        throw new ArgumentException(
                            $"Source '{item.SourceId}' occurs more than once in the revision vector.",
                            nameof(revisions)
                        );
                    }
                }

                entries[index] = new KeyValuePair<string, string?>(item.SourceId, item.Revision);
            }

            return new SmallReadOnlyDictionary<string?>(entries);
        }

        var values = new Dictionary<string, string?>(revisions.Length, StringComparer.Ordinal)
        {
            [first.SourceId] = first.Revision,
        };
        for (var index = 1; index < revisions.Length; index++)
        {
            var item = revisions[index];
            ArgumentException.ThrowIfNullOrWhiteSpace(item.SourceId);
            if (!values.TryAdd(item.SourceId, item.Revision))
            {
                throw new ArgumentException(
                    $"Source '{item.SourceId}' occurs more than once in the revision vector.",
                    nameof(revisions)
                );
            }
        }

        return new ReadOnlyDictionary<string, string?>(values);
    }

    private static IReadOnlyDictionary<string, StateRevisionVector> CreateNestedRevisionMapFromSpan(
        ReadOnlySpan<KeyValuePair<string, StateRevisionVector>> nestedRevisions
    )
    {
        if (nestedRevisions.IsEmpty)
        {
            return EmptyNestedRevisions;
        }

        var first = nestedRevisions[0];
        ArgumentException.ThrowIfNullOrWhiteSpace(first.Key);
        ArgumentNullException.ThrowIfNull(first.Value);
        if (nestedRevisions.Length == 1)
        {
            return new SingleEntryReadOnlyDictionary<StateRevisionVector>(first.Key, first.Value);
        }

        if (nestedRevisions.Length <= SmallDictionaryThreshold)
        {
            var entries = new KeyValuePair<string, StateRevisionVector>[nestedRevisions.Length];
            entries[0] = new KeyValuePair<string, StateRevisionVector>(first.Key, first.Value);
            for (var index = 1; index < nestedRevisions.Length; index++)
            {
                var item = nestedRevisions[index];
                ArgumentException.ThrowIfNullOrWhiteSpace(item.Key);
                ArgumentNullException.ThrowIfNull(item.Value);
                for (var existing = 0; existing < index; existing++)
                {
                    if (StringComparer.Ordinal.Equals(entries[existing].Key, item.Key))
                    {
                        throw new ArgumentException(
                            $"Source '{item.Key}' occurs more than once in the nested revision vectors.",
                            nameof(nestedRevisions)
                        );
                    }
                }

                entries[index] = new KeyValuePair<string, StateRevisionVector>(
                    item.Key,
                    item.Value
                );
            }

            return new SmallReadOnlyDictionary<StateRevisionVector>(entries);
        }

        var values = new Dictionary<string, StateRevisionVector>(
            nestedRevisions.Length,
            StringComparer.Ordinal
        )
        {
            [first.Key] = first.Value,
        };
        for (var index = 1; index < nestedRevisions.Length; index++)
        {
            var item = nestedRevisions[index];
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Key);
            ArgumentNullException.ThrowIfNull(item.Value);
            if (!values.TryAdd(item.Key, item.Value))
            {
                throw new ArgumentException(
                    $"Source '{item.Key}' occurs more than once in the nested revision vectors.",
                    nameof(nestedRevisions)
                );
            }
        }

        return new ReadOnlyDictionary<string, StateRevisionVector>(values);
    }

    private static IReadOnlyDictionary<string, string?> CreateRevisionMap(
        IEnumerable<StateRevision> revisions
    )
    {
        using var enumerator = revisions.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return EmptyRevisions;
        }

        var first = enumerator.Current;
        ArgumentException.ThrowIfNullOrWhiteSpace(first.SourceId);
        if (!enumerator.MoveNext())
        {
            return new SingleEntryReadOnlyDictionary<string?>(first.SourceId, first.Revision);
        }

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        values.Add(first.SourceId, first.Revision);
        do
        {
            var item = enumerator.Current;
            ArgumentException.ThrowIfNullOrWhiteSpace(item.SourceId);
            if (!values.TryAdd(item.SourceId, item.Revision))
            {
                throw new ArgumentException(
                    $"Source '{item.SourceId}' occurs more than once in the revision vector.",
                    nameof(revisions)
                );
            }
        } while (enumerator.MoveNext());

        return new ReadOnlyDictionary<string, string?>(values);
    }

    private static IReadOnlyDictionary<string, StateRevisionVector> CreateNestedRevisionMap(
        IEnumerable<KeyValuePair<string, StateRevisionVector>> nestedRevisions
    )
    {
        using var enumerator = nestedRevisions.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return EmptyNestedRevisions;
        }

        var first = enumerator.Current;
        ArgumentException.ThrowIfNullOrWhiteSpace(first.Key);
        ArgumentNullException.ThrowIfNull(first.Value);
        if (!enumerator.MoveNext())
        {
            return new SingleEntryReadOnlyDictionary<StateRevisionVector>(first.Key, first.Value);
        }

        var values = new Dictionary<string, StateRevisionVector>(StringComparer.Ordinal)
        {
            [first.Key] = first.Value,
        };
        do
        {
            var item = enumerator.Current;
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Key);
            ArgumentNullException.ThrowIfNull(item.Value);
            if (!values.TryAdd(item.Key, item.Value))
            {
                throw new ArgumentException(
                    $"Source '{item.Key}' occurs more than once in the nested revision vectors.",
                    nameof(nestedRevisions)
                );
            }
        } while (enumerator.MoveNext());

        return new ReadOnlyDictionary<string, StateRevisionVector>(values);
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

    private sealed class SingleEntryReadOnlyDictionary<TValue>(string key, TValue storedValue)
        : IReadOnlyDictionary<string, TValue>
    {
        public TValue this[string key] =>
            TryGetValue(key, out var entry)
                ? entry
                : throw new KeyNotFoundException($"Key '{key}' was not present in the dictionary.");

        public IEnumerable<string> Keys => EnumerateKeys();

        public IEnumerable<TValue> Values => EnumerateValues();

        public int Count => 1;

        public bool ContainsKey(string candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            return string.Equals(candidate, key, StringComparison.Ordinal);
        }

        public bool TryGetValue(string candidate, out TValue value)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            if (string.Equals(candidate, key, StringComparison.Ordinal))
            {
                value = storedValue;
                return true;
            }

            value = default!;
            return false;
        }

        public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator()
        {
            yield return new KeyValuePair<string, TValue>(key, storedValue);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private IEnumerable<string> EnumerateKeys()
        {
            yield return key;
        }

        private IEnumerable<TValue> EnumerateValues()
        {
            yield return storedValue;
        }
    }

    private sealed class SmallReadOnlyDictionary<TValue>(KeyValuePair<string, TValue>[] entries)
        : IReadOnlyDictionary<string, TValue>
    {
        public TValue this[string key] =>
            TryGetValue(key, out var entry)
                ? entry
                : throw new KeyNotFoundException($"Key '{key}' was not present in the dictionary.");

        public IEnumerable<string> Keys
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

        public bool ContainsKey(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            for (var index = 0; index < entries.Length; index++)
            {
                if (string.Equals(entries[index].Key, key, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public bool TryGetValue(string key, out TValue value)
        {
            ArgumentNullException.ThrowIfNull(key);
            for (var index = 0; index < entries.Length; index++)
            {
                if (string.Equals(entries[index].Key, key, StringComparison.Ordinal))
                {
                    value = entries[index].Value;
                    return true;
                }
            }

            value = default!;
            return false;
        }

        public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator() =>
            ((IEnumerable<KeyValuePair<string, TValue>>)entries).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => entries.GetEnumerator();
    }
}
