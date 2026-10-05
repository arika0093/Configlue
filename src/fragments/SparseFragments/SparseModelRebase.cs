using System.Collections;
using System.ComponentModel;
#if CONFIGLUE_FRAGMENT_RUNTIME
using Configlue.CompilerServices;
using ModelSchema = Configlue.ConfiglueModelSchema;
using MemberSchema = Configlue.ConfiglueMemberSchema;
using Fragment = Configlue.IConfiglueFragment;
using RebaseOutcome = Configlue.ConfiglueRebaseResult;
using RebaseConflict = Configlue.ConfiglueRebaseConflict;
using ConflictKind = Configlue.ConfiglueRebaseConflictKind;
using CollectionRebase = Configlue.ConfiglueCollectionRebase;
using MergeStrategy = Configlue.IConfiglueMergeStrategy;
using MergeRebaseStrategy = Configlue.IConfiglueMergeRebaseStrategy;

namespace Configlue;

#else
using ModelSchema = SparseFragments.SparseFragmentSchema;
using MemberSchema = SparseFragments.SparseFragmentMemberSchema;
using Fragment = SparseFragments.ISparseFragment;
using RebaseOutcome = SparseFragments.RebaseResult<SparseFragments.ISparseFragment>;
using RebaseConflict = SparseFragments.SparsePatchConflict;
using ConflictKind = SparseFragments.SparsePatchConflictKind;
using CollectionRebase = SparseFragments.SparseCollectionRebase;
using MergeStrategy = SparseFragments.ISparseMergeStrategy;
using MergeRebaseStrategy = SparseFragments.ISparseMergeRebaseStrategy;

namespace SparseFragments;

#endif

/// <summary>
/// Domain-neutral three-way rebase of a model edit. The algorithm depends only on generated schema metadata
/// and source-local fragments; callers translate the structured conflicts into their own error model.
/// </summary>
/// <remarks>Generated-code plumbing: referenced by emitted code, not hand-written callers.</remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
#if CONFIGLUE_FRAGMENT_RUNTIME
public static class ConfiglueFragmentRebase
#else
public static class SparseModelRebase
#endif
{
    /// <summary>Rebases a changes fragment from <paramref name="before"/>-to-<paramref name="desired"/> onto <paramref name="current"/>.</summary>
    /// <param name="schema">The generated model schema.</param>
    /// <param name="changes">The local changes fragment.</param>
    /// <param name="before">The baseline model.</param>
    /// <param name="desired">The locally edited model.</param>
    /// <param name="current">The newer model.</param>
    /// <param name="localWinsCollections">When true, collection members use the desired value wholesale.</param>
    /// <returns>The rebased changes fragment and any conflicts. Conflicting members fall back to the desired value.</returns>
    public static RebaseOutcome Rebase(
        ModelSchema schema,
        Fragment changes,
        object before,
        object desired,
        object current,
        bool localWinsCollections = false
    )
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(current);
        var conflicts = new List<RebaseConflict>();
        var rebased = RebaseCore(
            schema,
            changes,
            before,
            desired,
            current,
            localWinsCollections,
            [],
            conflicts
        );
        return new RebaseOutcome(rebased, conflicts);
    }

    /// <summary>Compares two member values using the same semantics as the rebase algorithm.</summary>
    public static bool AreEditValuesEqual(MemberSchema member, object? left, object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (
            left is IEnumerable leftValues
            && left is not string
            && right is IEnumerable rightValues
            && right is not string
        )
        {
            if (IsSetCollectionType(member.ValueType))
            {
                return new HashSet<object?>(leftValues.Cast<object?>()).SetEquals(
                    rightValues.Cast<object?>()
                );
            }

            return leftValues.Cast<object?>().SequenceEqual(rightValues.Cast<object?>());
        }

        return Equals(left, right);
    }

    private static Fragment RebaseCore(
        ModelSchema schema,
        Fragment changes,
        object before,
        object desired,
        object current,
        bool localWinsCollections,
        List<string> path,
        List<RebaseConflict> conflicts
    )
    {
        foreach (var change in changes.EnumeratePresentMembers())
        {
            if (!TryGetMember(schema, change.Id, out var member))
            {
                throw new InvalidOperationException(
                    $"Generated schema '{schema.ModelType}' has no member with id {change.Id}."
                );
            }

            path.Add(member.Name);
            try
            {
                var beforeValue = member.GetValue?.Invoke(before);
                var desiredValue = member.GetValue?.Invoke(desired);
                var currentValue = member.GetValue?.Invoke(current);
                if (
                    member.NestedSchemaFactory is not null
                    && change.Value is Fragment nestedChanges
                    && beforeValue is not null
                    && desiredValue is not null
                    && currentValue is not null
                )
                {
                    var nested = RebaseCore(
                        member.NestedSchemaFactory(),
                        nestedChanges,
                        beforeValue,
                        desiredValue,
                        currentValue,
                        localWinsCollections,
                        path,
                        conflicts
                    );
                    changes = changes.WithMember(member.Id, nested);
                    continue;
                }

                if (GetMergeStrategy(member) is { } mergeStrategy)
                {
                    if (mergeStrategy is not MergeRebaseStrategy rebaseStrategy)
                    {
                        conflicts.Add(
                            CreateConflict(
                                path,
                                ConflictKind.CustomStrategy,
                                beforeValue,
                                desiredValue,
                                currentValue,
                                "The custom merge strategy does not support edit rebasing."
                            )
                        );
                        changes = changes.WithMember(member.Id, desiredValue);
                        continue;
                    }

                    if (
#if CONFIGLUE_FRAGMENT_RUNTIME
                        rebaseStrategy.TryRebaseObject(
#else
                        rebaseStrategy.TryRebaseObject(
#endif
                            beforeValue,
                            desiredValue,
                            currentValue,
                            out var rebasedValue,
                            out var reason
                        )
                    )
                    {
                        changes = changes.WithMember(member.Id, rebasedValue);
                    }
                    else
                    {
                        conflicts.Add(
                            CreateConflict(
                                path,
                                ConflictKind.CustomStrategy,
                                beforeValue,
                                desiredValue,
                                currentValue,
                                reason
                            )
                        );
                        changes = changes.WithMember(member.Id, desiredValue);
                    }

                    continue;
                }

                if (member.CollectionValueFactory is not null)
                {
                    var rebasedCollection = RebaseCollectionEdit(
                        member,
                        beforeValue,
                        desiredValue,
                        currentValue,
                        localWinsCollections,
                        path,
                        conflicts
                    );
                    if (rebasedCollection is not null)
                    {
                        changes = changes.WithMember(member.Id, rebasedCollection);
                    }

                    continue;
                }

                if (
                    !AreEditValuesEqual(member, beforeValue, currentValue)
                    && !AreEditValuesEqual(member, desiredValue, currentValue)
                )
                {
                    conflicts.Add(
                        CreateConflict(
                            path,
                            ConflictKind.Scalar,
                            beforeValue,
                            desiredValue,
                            currentValue,
                            $"The configuration edit conflicts with a concurrent change to '{string.Join(".", path)}'."
                        )
                    );
                    changes = changes.WithMember(member.Id, desiredValue);
                }
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }

        return changes;
    }

    private static object? RebaseCollectionEdit(
        MemberSchema member,
        object? beforeValue,
        object? desiredValue,
        object? currentValue,
        bool localWinsCollections,
        List<string> path,
        List<RebaseConflict> conflicts
    )
    {
        if (localWinsCollections)
        {
            return desiredValue;
        }

        if (
            member.MergeMode is MergeMode.Append or MergeMode.SetUnion
            && beforeValue is IEnumerable beforeEnumerable
            && beforeValue is not string
            && desiredValue is IEnumerable desiredEnumerable
            && desiredValue is not string
            && currentValue is IEnumerable currentEnumerable
            && currentValue is not string
        )
        {
            var before = beforeEnumerable.Cast<object?>().ToList();
            var desired = desiredEnumerable.Cast<object?>().ToList();
            var current = currentEnumerable.Cast<object?>().ToList();
            IReadOnlyList<object?> rebased;
            string? reason;
            var succeeded =
                member.MergeMode == MergeMode.Append
                    ? CollectionRebase.TryRebaseAppend(
                        before,
                        desired,
                        current,
                        (left, right) => AreEditValuesEqual(member, left, right),
                        out rebased,
                        out reason
                    )
                    : CollectionRebase.TryRebaseSetUnion(
                        before,
                        desired,
                        current,
                        (left, right) => AreEditValuesEqual(member, left, right),
                        out rebased,
                        out reason
                    );
            if (succeeded)
            {
                return member.CollectionValueFactory!(rebased);
            }

            conflicts.Add(
                CreateConflict(
                    path,
                    member.MergeMode == MergeMode.Append
                        ? ConflictKind.CollectionAppend
                        : ConflictKind.CollectionSetUnion,
                    beforeValue,
                    desiredValue,
                    currentValue,
                    reason
                )
            );
            return desiredValue;
        }

        if (
            !AreEditValuesEqual(member, beforeValue, currentValue)
            && !AreEditValuesEqual(member, desiredValue, currentValue)
        )
        {
            conflicts.Add(
                CreateConflict(
                    path,
                    ConflictKind.Scalar,
                    beforeValue,
                    desiredValue,
                    currentValue,
                    $"The configuration edit conflicts with a concurrent change to '{string.Join(".", path)}'."
                )
            );
            return desiredValue;
        }

        return null;
    }

    private static bool TryGetMember(ModelSchema schema, int memberId, out MemberSchema member) =>
        schema.TryGetMember(memberId, out member);

    private static bool IsSetCollectionType(Type valueType)
    {
        if (!valueType.IsGenericType)
        {
            return false;
        }

        var definition = valueType.GetGenericTypeDefinition();
        return definition == typeof(HashSet<>)
            || definition == typeof(ISet<>)
            || definition == typeof(SortedSet<>)
            || definition.FullName == "System.Collections.Generic.IReadOnlySet`1";
    }

    private static MergeStrategy? GetMergeStrategy(MemberSchema member) =>
#if CONFIGLUE_FRAGMENT_RUNTIME
        member.MergeStrategy;
#else
        member.CollectionMergeStrategy;
#endif

    private static RebaseConflict CreateConflict(
        IEnumerable<string> path,
        ConflictKind kind,
        object? before,
        object? desired,
        object? current,
        string? reason
    ) =>
#if CONFIGLUE_FRAGMENT_RUNTIME
        new(path, kind, before, desired, current, reason);
#else
        new(
            path,
            kind,
            Optional<object?>.Present(before),
            Optional<object?>.Present(desired),
            Optional<object?>.Present(current),
            reason
        );
#endif
}
