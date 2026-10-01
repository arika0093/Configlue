using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Configlue.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Configlue;

internal sealed partial class ConfiglueRuntime<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    /// <inheritdoc />
    public ValueTask<EditSession<TModel>> OpenEditSessionAsync(
        CancellationToken cancellationToken = default
    ) =>
        OpenEditSessionCoreAsync(null, cancellationToken, pinnedSubject: null, upstreamState: this);

    /// <inheritdoc />
    public ValueTask<EditSession<TModel>> OpenEditSessionAsync(
        StateWritePlan writePlan,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        return OpenEditSessionCoreAsync(
            writePlan,
            cancellationToken,
            pinnedSubject: null,
            upstreamState: this
        );
    }

    private ValueTask<EditSession<TModel>> OpenEditSessionForSubjectAsync(
        IConfiglueSubject subject,
        StateWritePlan? writePlan,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(subject);
        return OpenEditSessionCoreAsync(
            writePlan,
            cancellationToken,
            pinnedSubject: subject,
            upstreamState: new SubjectBoundOptions(this, subject)
        );
    }

    private async ValueTask<EditSession<TModel>> OpenEditSessionCoreAsync(
        StateWritePlan? writePlan,
        CancellationToken cancellationToken,
        IConfiglueSubject? pinnedSubject,
        IReadOnlyState<TModel>? upstreamState
    )
    {
        using var operation = EnterOperation();
        using IDisposable? subjectScope = pinnedSubject is null
            ? null
            : EnterSubject(pinnedSubject);
        var resolvedState = await ResolveCoreAsync(
                null,
                cancellationToken,
                captureContributions: true
            )
            .ConfigureAwait(false);
        var resolved = resolvedState.Result;
        if (resolved.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException(
                $"Configuration state could not be read: {resolved.Status}."
            );
        }

        var effectiveWritePlan = _writePlan.OverrideWith(writePlan ?? StateWritePlan.Empty);
        if (
            effectiveWritePlan.DefaultSourceId is null
            && effectiveWritePlan.PropertyRoutes.Count == 0
        )
        {
            throw new InvalidOperationException(
                "No writable state source is registered for this model."
            );
        }

        if (effectiveWritePlan.PropertyRoutes.Count > 0)
        {
            ValidateWritePlan(effectiveWritePlan);
        }

        var details = BuildDetailsSnapshot(resolvedState);
        var draft = CloneModel(resolved.Value!);
        var sessionStart = new StateSnapshot<TModel>(CloneModel(resolved.Value!), details);
        var baseline = CloneModel(resolved.Value!);
        var defaultValue = CloneModel(FromFragment(EmptyFragment));
        var expectedRevisions = resolved.Revisions;

        async ValueTask<StateWriteReceipt> SaveSessionValueAsync(
            TModel value,
            CancellationToken token
        )
        {
            using IDisposable? saveScope = pinnedSubject is null
                ? null
                : EnterSubject(pinnedSubject);
            var latestState = await ResolveCoreAsync(null, token, captureContributions: true)
                .ConfigureAwait(false);
            var latest = latestState.Result;
            if (latest.Status != StateReadStatus.Success)
            {
                throw new InvalidOperationException(
                    $"Configuration state could not be read before saving: {latest.Status}."
                );
            }

            var hasRevisionChanges = !HaveSameRevisions(expectedRevisions, latest.Revisions);
            var saveBaseline = baseline;
            var saveValue = value;
            var saveContributions = latestState.Contributions;
            var saveRevisions = latest.Revisions;
            if (hasRevisionChanges)
            {
                saveBaseline = latest.Value!;
                saveValue = RebaseConfigurationEdit(baseline, value, saveBaseline);
            }

            var writeResult = await WriteChangesToSourcesAsync(
                    saveBaseline,
                    saveValue,
                    saveRevisions,
                    saveContributions,
                    effectiveWritePlan,
                    token
                )
                .ConfigureAwait(false);

            baseline = value;
            expectedRevisions = latest.Revisions;
            return writeResult;
        }

        async ValueTask<StateSnapshot<TModel>> ResolveSessionUpstreamAsync(CancellationToken token)
        {
            using IDisposable? resolveScope = pinnedSubject is null
                ? null
                : EnterSubject(pinnedSubject);
            var latestState = await ResolveCoreAsync(null, token, captureContributions: true)
                .ConfigureAwait(false);
            var latest = latestState.Result;
            if (latest.Status != StateReadStatus.Success || latest.Value is null)
            {
                throw new InvalidOperationException(
                    $"Configuration state could not be read before rebasing: {latest.Status}."
                );
            }

            baseline = latest.Value;
            expectedRevisions = latest.Revisions;
            return new StateSnapshot<TModel>(latest.Value, BuildDetailsSnapshot(latestState));
        }

        return new EditSession<TModel>(
            draft,
            sessionStart,
            SaveSessionValueAsync,
            RebaseConfigurationEdit,
            static (value, baselineValue) => !Diff(baselineValue, value).IsEmpty,
            ResolveSessionUpstreamAsync,
            CloneModel,
            defaultValue,
            upstreamState
        );
    }

    private TModel RebaseConfigurationEdit(TModel before, TModel desired, TModel current)
    {
        var changes = Diff(before, desired);
        if (changes.IsEmpty)
        {
            return current;
        }

        var rebased = RebaseChanges(ModelSchema, changes, before, desired, current, []);
        var currentFragment = ToFragment(current);
        if (currentFragment.ApplyChanges((TFragment)rebased) is not TFragment updated)
        {
            throw new InvalidOperationException(
                "The rebased edit produced an incompatible fragment."
            );
        }

        var result = FromFragment(updated);
        return result;
    }

    private IConfiglueFragment RebaseChanges(
        ConfiglueModelSchema schema,
        IConfiglueFragment changes,
        object before,
        object desired,
        object current,
        List<string> path
    )
    {
        foreach (var change in changes.EnumeratePresentMembers())
        {
            if (!TryGetMember(schema, change.Id, out var member))
            {
                throw new InvalidOperationException(
                    $"Generated schema '{schema.Id}' has no member with id {change.Id}."
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
                    && change.Value is IConfiglueFragment nestedChanges
                    && beforeValue is not null
                    && desiredValue is not null
                    && currentValue is not null
                )
                {
                    var nested = RebaseChanges(
                        member.NestedSchemaFactory(),
                        nestedChanges,
                        beforeValue,
                        desiredValue,
                        currentValue,
                        path
                    );
                    changes = changes.WithMember(member.Id, nested);
                    continue;
                }

                if (member.MergeStrategy is { } mergeStrategy)
                {
                    if (
                        !mergeStrategy.TryRebase(
                            beforeValue,
                            desiredValue,
                            currentValue,
                            out var rebasedValue,
                            out var reason
                        )
                    )
                    {
                        if (_writeConflictResolution == WriteConflictResolution.FailOnConflict)
                        {
                            throw LogConflict(
                                reason
                                    ?? $"The custom merge strategy could not rebase the edit to '{string.Join(".", path)}'."
                            );
                        }

                        changes = changes.WithMember(member.Id, desiredValue);
                        continue;
                    }

                    changes = changes.WithMember(member.Id, rebasedValue);
                    continue;
                }

                if (member.CollectionValueFactory is not null)
                {
                    var rebasedCollection = RebaseCollectionEdit(
                        member,
                        beforeValue,
                        desiredValue,
                        currentValue,
                        string.Join(".", path)
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
                    && _writeConflictResolution == WriteConflictResolution.FailOnConflict
                )
                {
                    throw LogConflict(
                        $"The configuration edit conflicts with a concurrent change to '{string.Join(".", path)}'."
                    );
                }
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }

        return changes;
    }

    private object? RebaseCollectionEdit(
        ConfiglueMemberSchema member,
        object? beforeValue,
        object? desiredValue,
        object? currentValue,
        string propertyPath
    )
    {
        if (_writeConflictResolution == WriteConflictResolution.LastWriteWins)
        {
            return desiredValue;
        }

        if (
            beforeValue is not System.Collections.IEnumerable beforeEnumerable
            || beforeValue is string
            || desiredValue is not System.Collections.IEnumerable desiredEnumerable
            || desiredValue is string
            || currentValue is not System.Collections.IEnumerable currentEnumerable
            || currentValue is string
        )
        {
            if (
                !AreEditValuesEqual(member, beforeValue, currentValue)
                && !AreEditValuesEqual(member, desiredValue, currentValue)
            )
            {
                throw LogConflict(
                    $"The configuration edit conflicts with a concurrent change to '{propertyPath}'."
                );
            }

            return null;
        }

        var before = beforeEnumerable.Cast<object?>().ToList();
        var desired = desiredEnumerable.Cast<object?>().ToList();
        var current = currentEnumerable.Cast<object?>().ToList();
        if (member.MergeMode == MergeMode.Append)
        {
            if (desired.Count < before.Count || !desired.Take(before.Count).SequenceEqual(before))
            {
                if (
                    !AreEditValuesEqual(member, current, before)
                    && !AreEditValuesEqual(member, current, desired)
                )
                {
                    throw LogConflict(
                        $"The configuration edit conflicts with a concurrent change to append-merged member '{propertyPath}'."
                    );
                }

                return member.CollectionValueFactory!(desired);
            }

            if (current.Count < before.Count || !current.Take(before.Count).SequenceEqual(before))
            {
                throw LogConflict(
                    $"The configuration edit cannot reapply its append to '{propertyPath}' because the existing collection prefix changed."
                );
            }

            var additions = desired.Skip(before.Count);
            return member.CollectionValueFactory!(current.Concat(additions));
        }

        if (member.MergeMode == MergeMode.SetUnion)
        {
            var removed = before.Where(value => !desired.Contains(value)).ToArray();
            if (
                removed.Length > 0
                && !AreEditValuesEqual(member, current, before)
                && !AreEditValuesEqual(member, current, desired)
            )
            {
                throw LogConflict(
                    $"The configuration edit conflicts with a concurrent change to set-union member '{propertyPath}'."
                );
            }

            if (removed.Length > 0)
            {
                return member.CollectionValueFactory!(desired);
            }

            var rebased = current.ToList();
            foreach (
                var value in desired
                    .Where(value => !before.Contains(value))
                    .Where(value => !rebased.Contains(value))
            )
            {
                rebased.Add(value);
            }

            return member.CollectionValueFactory!(rebased);
        }

        if (
            !AreEditValuesEqual(member, beforeValue, currentValue)
            && !AreEditValuesEqual(member, desiredValue, currentValue)
        )
        {
            throw LogConflict(
                $"The configuration edit conflicts with a concurrent change to '{propertyPath}'."
            );
        }

        return null;
    }

    private static bool AreEditValuesEqual(
        ConfiglueMemberSchema member,
        object? left,
        object? right
    )
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (
            left is System.Collections.IEnumerable leftValues
            && left is not string
            && right is System.Collections.IEnumerable rightValues
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

    private static bool IsSetCollectionType(Type valueType)
    {
        if (!valueType.IsGenericType)
        {
            return false;
        }

        var definition = valueType.GetGenericTypeDefinition();
        return definition == typeof(HashSet<>)
            || definition == typeof(ISet<>)
            || definition == typeof(IReadOnlySet<>);
    }
}
