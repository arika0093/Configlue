using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// Generated model-operation fast paths shared by runtime coordinators.
///
/// The delegates below are the source-generated operation table registered for the
/// closed model/fragment pair. Coordinators call through them exactly as the former
/// <c>ConfiglueRuntime</c> static helpers did; no interface dispatch is added.
/// </summary>
internal static class RuntimeModel<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    internal static ConfiglueModelOperations<TModel, TFragment> Operations =>
        ConfiglueModelOperations<TModel, TFragment>.Current;

    internal static ConfiglueModelSchema Schema => Operations.Schema;

    internal static TFragment EmptyFragment => Operations.EmptyFragment;

    internal static ConfiglueResourceContext DefaultResourceContext =>
        ConfiglueResourceContext.Default with
        {
            ModelId = Schema.Id,
        };

    internal static TFragment ToFragment(TModel value) => Operations.ToFragment(value);

    internal static TFragment Diff(TModel before, TModel after) => Operations.Diff(before, after);

    internal static TModel FromFragment(TFragment value) => Operations.FromFragment(value);

    internal static TFragment CloneFragment(TFragment value) =>
        value is IConfiglueDeepCloneable<TFragment> cloneable ? cloneable.DeepClone() : value;
}

/// <summary>
/// Stateless helpers shared by runtime coordinators.
///
/// These are pure functions over resolution/write state. They own no mutable state,
/// so keeping them in one place avoids duplicating logic across coordinators.
/// </summary>
internal static class RuntimeState
{
    internal static bool TryGetMember(
        ConfiglueModelSchema schema,
        int memberId,
        out ConfiglueMemberSchema member
    ) => schema.TryGetMember(memberId, out member);

    internal static bool HaveSameRevisions(StateRevisionVector? left, StateRevisionVector? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Revisions.Count != right.Revisions.Count)
        {
            return false;
        }

        return left.Revisions.All(pair =>
                right.TryGetRevision(pair.Key, out var revision)
                && string.Equals(pair.Value, revision, StringComparison.Ordinal)
            )
            && left.NestedRevisions.Count == right.NestedRevisions.Count
            && left.NestedRevisions.All(pair =>
                right.TryGetNestedRevisions(pair.Key, out var nested)
                && HaveSameRevisions(pair.Value, nested)
            );
    }

    internal static bool CanFallBack(StateFallbackCondition condition, StateReadStatus status) =>
        status switch
        {
            StateReadStatus.NotFound => (condition & StateFallbackCondition.NotFound) != 0,
            StateReadStatus.Unavailable => (condition & StateFallbackCondition.Unavailable) != 0,
            // Malformed payloads fail visibly and never fall back (issue #312).
            StateReadStatus.InvalidPayload => false,
            _ => false,
        };

    internal static TaskCompletionSource NewTopologySignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static bool TryGetFragmentValue(
        IConfiglueFragment fragment,
        IReadOnlyList<string> path,
        out object? value
    )
    {
        IConfiglueFragment current = fragment;
        for (var index = 0; index < path.Count; index++)
        {
            var found = false;
            object? currentValue = null;
            foreach (var member in current.EnumeratePresentMembersFast())
            {
                if (!string.Equals(member.Name, path[index], StringComparison.Ordinal))
                {
                    continue;
                }

                currentValue = member.Value;
                found = true;
                break;
            }

            if (!found)
            {
                value = null;
                return false;
            }

            if (index == path.Count - 1)
            {
                value = currentValue;
                return true;
            }

            if (currentValue is not IConfiglueFragment nested)
            {
                value = null;
                return false;
            }

            current = nested;
        }

        value = null;
        return false;
    }

    internal static StateRevisionVector CreateRevisionVector(
        StateRevision[]? revisions,
        StateRevision singleRevision,
        int revisionCount,
        KeyValuePair<SourceId, StateRevisionVector>[]? nestedRevisions,
        int nestedRevisionCount
    )
    {
        if (revisions is null)
        {
            if (revisionCount == 0)
            {
                return StateRevisionVector.FromSpan(ReadOnlySpan<StateRevision>.Empty);
            }

            var nested = nestedRevisionCount > 0 ? nestedRevisions![0].Value : null;
            return StateRevisionVector.FromSingle(singleRevision, nested);
        }

        return StateRevisionVector.FromSpan(
            revisions.AsSpan(0, revisionCount),
            nestedRevisions is null ? default : nestedRevisions.AsSpan(0, nestedRevisionCount)
        );
    }

    internal static StateConflictException NewConflict(
        RuntimeDiagnosticRecorder diagnostics,
        string message
    )
    {
        var exception = new StateConflictException(message);
        diagnostics.Record(
            ConfiglueDiagnosticEventKind.WriteConflict,
            errorCategory: typeof(StateConflictException).FullName
        );
        return exception;
    }
}

/// <summary>
/// Model clone policy for one runtime.
///
/// The optional custom strategy is fixed at construction; every coordinator that
/// hands a model to callers (reads, watch notifications, edit sessions) clones
/// through this owner so the validation rule stays in one place.
/// </summary>
internal sealed class RuntimeModelCloner<TModel, TFragment>(Func<TModel, TModel>? cloneStrategy)
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    internal TModel Clone(TModel value)
    {
        var clone = cloneStrategy is null ? value.DeepClone() : cloneStrategy(value);
        if (clone is null || ReferenceEquals(clone, value))
        {
            throw new InvalidOperationException(
                "The model clone strategy must return a non-null, distinct model instance."
            );
        }

        return clone;
    }
}

/// <summary>Reader for the synthetic model-defaults contribution used during resolution.</summary>
internal sealed class RuntimeModelDefaultsReader<TFragment>(TFragment fragment)
    : ISourceReader<TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    public ValueTask<StateReadResult<TFragment>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        _ = context;
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<StateReadResult<TFragment>>(
            StateReadResult<TFragment>.Success(fragment)
        );
    }
}
