using System.Collections;

namespace Configlue;

/// <summary>The reason a configuration edit could not be reconciled with a concurrent change.</summary>
public enum ConfiglueRebaseConflictKind
{
    /// <summary>A scalar member was changed concurrently.</summary>
    Scalar,

    /// <summary>A nested contribution was changed concurrently.</summary>
    Nested,

    /// <summary>An append-merged collection was changed concurrently.</summary>
    CollectionAppend,

    /// <summary>A set-union member was changed concurrently.</summary>
    CollectionSetUnion,

    /// <summary>A custom merge strategy reported a conflict.</summary>
    CustomStrategy,
}

/// <summary>Structured information about one configuration rebase conflict.</summary>
public sealed class ConfiglueRebaseConflict
{
    /// <summary>Initializes a new conflict.</summary>
    /// <param name="path">The member path from the root model.</param>
    /// <param name="kind">The conflict kind.</param>
    /// <param name="baseValue">The baseline value.</param>
    /// <param name="localValue">The desired (local) value.</param>
    /// <param name="currentValue">The current value.</param>
    /// <param name="reason">An optional human-readable reason.</param>
    public ConfiglueRebaseConflict(
        IEnumerable<string> path,
        ConfiglueRebaseConflictKind kind,
        object? baseValue,
        object? localValue,
        object? currentValue,
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

    /// <summary>The member path from the root model.</summary>
    public IReadOnlyList<string> Path { get; }

    /// <summary>The dotted member path.</summary>
    public string PathText => string.Join(".", Path);

    /// <summary>The conflict kind.</summary>
    public ConfiglueRebaseConflictKind Kind { get; }

    /// <summary>The baseline value.</summary>
    public object? BaseValue { get; }

    /// <summary>The desired (local) value.</summary>
    public object? LocalValue { get; }

    /// <summary>The current value.</summary>
    public object? CurrentValue { get; }

    /// <summary>An optional human-readable reason.</summary>
    public string? Reason { get; }
}

/// <summary>The result of rebasing a configuration edit onto a newer model.</summary>
public sealed class ConfiglueRebaseResult
{
    /// <summary>Initializes a rebase result.</summary>
    /// <param name="rebased">The rebased changes fragment.</param>
    /// <param name="conflicts">The detected conflicts.</param>
    public ConfiglueRebaseResult(
        IConfiglueFragment rebased,
        IEnumerable<ConfiglueRebaseConflict> conflicts
    )
    {
        ArgumentNullException.ThrowIfNull(rebased);
        ArgumentNullException.ThrowIfNull(conflicts);
        Rebased = rebased;
        Conflicts = Array.AsReadOnly(conflicts.ToArray());
    }

    /// <summary>The rebased changes fragment.</summary>
    public IConfiglueFragment Rebased { get; }

    /// <summary>The detected conflicts.</summary>
    public IReadOnlyList<ConfiglueRebaseConflict> Conflicts { get; }

    /// <summary>Whether any conflict was detected.</summary>
    public bool HasConflicts => Conflicts.Count > 0;
}
