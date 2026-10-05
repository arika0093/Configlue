using System.ComponentModel;

namespace SparseFragments;

/// <summary>The reason a rebased patch could not be reconciled with a concurrent change.</summary>
/// <remarks>Advanced vocabulary: patch rebase outcomes.</remarks>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public enum SparsePatchConflictKind
{
    /// <summary>The whole contribution was changed concurrently.</summary>
    WholeContribution,

    /// <summary>A nested contribution was changed concurrently.</summary>
    Nested,

    /// <summary>A scalar member was changed concurrently.</summary>
    Scalar,

    /// <summary>An append-merged collection was changed concurrently.</summary>
    CollectionAppend,

    /// <summary>A set-union member was changed concurrently.</summary>
    CollectionSetUnion,

    /// <summary>A custom merge strategy reported a conflict.</summary>
    CustomStrategy,
}

/// <summary>Structured, domain-neutral information about one rebase conflict.</summary>
/// <remarks>Advanced vocabulary: patch rebase outcomes.</remarks>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public sealed class SparsePatchConflict
{
    /// <summary>Initializes a new conflict.</summary>
    /// <param name="path">The member path from the root contribution.</param>
    /// <param name="kind">The conflict kind.</param>
    /// <param name="baseValue">The baseline presence-aware value.</param>
    /// <param name="localValue">The desired (local) presence-aware value.</param>
    /// <param name="currentValue">The current presence-aware value.</param>
    /// <param name="reason">An optional human-readable reason.</param>
    public SparsePatchConflict(
        IEnumerable<string> path,
        SparsePatchConflictKind kind,
        Optional<object?> baseValue,
        Optional<object?> localValue,
        Optional<object?> currentValue,
        string? reason
    )
    {
        ArgumentNullException.ThrowIfNull(path);
        Path = Array.AsReadOnly(path.ToArray());
        Kind = kind;
        BaseValue = baseValue;
        LocalValue = localValue;
        CurrentValue = currentValue;
        Reason = reason;
    }

    /// <summary>The member path from the root contribution.</summary>
    public IReadOnlyList<string> Path { get; }

    /// <summary>The dotted member path, or an empty string for the root contribution.</summary>
    public string PathText => string.Join(".", Path);

    /// <summary>The conflict kind.</summary>
    public SparsePatchConflictKind Kind { get; }

    /// <summary>The baseline presence-aware value.</summary>
    public Optional<object?> BaseValue { get; }

    /// <summary>The desired (local) presence-aware value.</summary>
    public Optional<object?> LocalValue { get; }

    /// <summary>The current presence-aware value.</summary>
    public Optional<object?> CurrentValue { get; }

    /// <summary>An optional human-readable reason.</summary>
    public string? Reason { get; }

    /// <summary>Returns a copy of this conflict with one path segment prepended.</summary>
    public SparsePatchConflict WithPathPrefix(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var prefix = new string[Path.Count + 1];
        prefix[0] = segment;
        for (var index = 0; index < Path.Count; index++)
        {
            prefix[index + 1] = Path[index];
        }

        return new SparsePatchConflict(prefix, Kind, BaseValue, LocalValue, CurrentValue, Reason);
    }
}

/// <summary>The result of rebasing a patch onto a newer sparse state.</summary>
/// <typeparam name="TPatch">The generated patch type.</typeparam>
/// <remarks>Advanced vocabulary: patch rebase outcomes.</remarks>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public sealed class RebaseResult<TPatch>
{
    /// <summary>Initializes a rebase result.</summary>
    /// <param name="patch">The rebased patch, excluding conflicting members.</param>
    /// <param name="conflicts">The detected conflicts.</param>
    public RebaseResult(TPatch patch, IEnumerable<SparsePatchConflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        Patch = patch;
        Conflicts = Array.AsReadOnly(conflicts.ToArray());
    }

    /// <summary>The rebased patch, excluding conflicting members.</summary>
    public TPatch Patch { get; }

    /// <summary>The detected conflicts.</summary>
    public IReadOnlyList<SparsePatchConflict> Conflicts { get; }

    /// <summary>Whether any conflict was detected.</summary>
    public bool HasConflicts => Conflicts.Count > 0;

    /// <summary>Creates a conflict-free rebase result.</summary>
    public static RebaseResult<TPatch> Success(TPatch patch) => new(patch, []);
}
