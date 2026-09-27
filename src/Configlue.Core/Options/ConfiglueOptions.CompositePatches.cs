using System.Diagnostics;

namespace Configlue;

public sealed partial class ConfiglueOptions<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private async ValueTask PrepareCompositePatchAsync(
        StateSource<TFragment> source,
        CompositeStateSource<TFragment> composite,
        StateSourcePatch patchRequest,
        StateReadResult<TFragment> current,
        ResolvedState baseline,
        ConfiglueModelSchema modelSchema,
        Dictionary<string, StateReadResult<TFragment>> replacements,
        Dictionary<string, StateSourceWriteResult> noOpResults,
        List<(
            StateSource<TFragment> Source,
            IStateWriter<TFragment> Writer,
            StateWriteRequest<TFragment> Request,
            ResourceId? ResourceId,
            IResourceBatchWriter? BatchWriter,
            ResourceWriteMutation? Mutation
        )> writePlans,
        CancellationToken cancellationToken
    )
    {
        if (patchRequest.Patch is not IConfiglueMemberPatch memberPatch)
        {
            throw new NotSupportedException(
                $"Patch for composite source '{source.Id}' must support member selection."
            );
        }

        var invalidMemberRoute = composite.WritePlan.PropertyRoutes.Keys.FirstOrDefault(
            memberRoute => !IsValidMemberPath(modelSchema, memberRoute.Split('.'))
        );
        if (invalidMemberRoute is not null)
        {
            throw new InvalidOperationException(
                $"Composite write route '{invalidMemberRoute}' does not match a model member path."
            );
        }

        var routedPatches = new Dictionary<string, IConfigluePatch>(StringComparer.Ordinal);
        if (patchRequest.Patch is FragmentChangesPatch fragmentPatch)
        {
            var routedChanges = PartitionCompositeChanges(
                modelSchema,
                fragmentPatch.Changes,
                composite,
                []
            );
            foreach (var (componentId, componentChanges) in routedChanges)
            {
                routedPatches.Add(
                    componentId,
                    new FragmentChangesPatch((TFragment)componentChanges)
                );
            }
        }
        else if (patchRequest.Patch is IConfiglueRoutablePatch routablePatch)
        {
            foreach (
                var (componentId, componentPatch) in routablePatch.Route(
                    composite.WritePlan,
                    composite.DefaultWriteSourceId
                )
            )
            {
                routedPatches.Add(componentId, componentPatch);
            }
        }
        else
        {
            var routedMemberIds = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (var member in modelSchema.Members)
            {
                var selected = memberPatch.SelectMembers([member.Id]);
                if (selected.IsEmpty)
                {
                    continue;
                }

                if (composite.HasWriteRouteBelow(member.Name))
                {
                    throw new NotSupportedException(
                        $"Patch for nested member '{member.Name}' must use a model edit so its nested changes can be routed."
                    );
                }

                var component = composite.ResolveWriteComponent(member.Name);
                if (!routedMemberIds.TryGetValue(component.Id, out var memberIds))
                {
                    memberIds = [];
                    routedMemberIds.Add(component.Id, memberIds);
                }

                memberIds.Add(member.Id);
            }

            foreach (var (componentId, memberIds) in routedMemberIds)
            {
                routedPatches.Add(componentId, memberPatch.SelectMembers(memberIds.ToArray()));
            }
        }

        if (routedPatches.Count == 0)
        {
            noOpResults.Add(
                source.Id,
                new StateSourceWriteResult(source.Id, source.ResourceId, current.Revision)
            );
            return;
        }

        if (
            baseline.Result.Revisions is null
            || !baseline.Result.Revisions.TryGetNestedRevisions(source.Id, out var nestedBaseline)
            || nestedBaseline is null
        )
        {
            throw LogConflict(
                $"Composite source '{source.Id}' has no component revision baseline."
            );
        }

        var componentOverrides = new Dictionary<string, TFragment>(StringComparer.Ordinal);
        foreach (var (componentId, componentPatch) in routedPatches)
        {
            var component = composite.Components.First(item =>
                string.Equals(item.Id, componentId, StringComparison.Ordinal)
            );
            var componentCurrent = (
                await component.Reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ).FromSource(component.Id, component.PhysicalOrigin);
            if (componentCurrent.Status == StateReadStatus.Unavailable)
            {
                throw new InvalidOperationException(
                    $"Cannot safely patch configuration because component '{component.Id}' is unavailable."
                );
            }

            var currentComponentRevisions =
                componentCurrent.Revisions
                ?? new StateRevisionVector([
                    new StateRevision(component.Id, componentCurrent.Revision),
                ]);
            if (!HaveSameRevisions(nestedBaseline, currentComponentRevisions))
            {
                throw LogConflict(
                    $"Component source '{component.Id}' changed while the patch batch was being prepared."
                );
            }

            var componentFragment = componentCurrent.Status switch
            {
                StateReadStatus.NotFound => TFragment.Empty,
                StateReadStatus.Success => componentCurrent.Value
                    ?? throw new InvalidOperationException(
                        $"Component source '{component.Id}' returned a null fragment."
                    ),
                _ => throw new InvalidOperationException(
                    $"Component source '{component.Id}' could not be patched: {componentCurrent.Status}."
                ),
            };
            if (componentCurrent.Schema is { } componentSchema)
            {
                componentFragment = await MigrateAsync(
                        componentFragment,
                        componentSchema,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            if (componentPatch.Apply(componentFragment) is not TFragment patchedComponent)
            {
                throw new InvalidOperationException(
                    $"The patch for component '{component.Id}' returned an incompatible fragment."
                );
            }

            patchedComponent = CloneFragment(patchedComponent);

            componentOverrides.Add(component.Id, patchedComponent);
            var componentRequest = new StateWriteRequest<TFragment>(
                patchedComponent,
                componentCurrent.Revision,
                CheckRevision: true
            );
            var componentResourceId = component.ResourceId;
            IResourceBatchWriter? componentBatchWriter = null;
            ResourceWriteMutation? componentMutation = null;
            ResourceId? componentParticipantResourceId = null;
            if (
                component.Writer is IAsyncStateWriteBatchParticipant<TFragment>
                {
                    CanPrepareBatchWrite: true,
                } componentAsyncParticipant
            )
            {
                var batchPlan = await componentAsyncParticipant
                    .TryCreateBatchWriteAsync(componentRequest, cancellationToken)
                    .ConfigureAwait(false);
                if (batchPlan is { } prepared)
                {
                    componentParticipantResourceId = prepared.ResourceId;
                    componentBatchWriter = prepared.BatchWriter;
                    componentMutation = prepared.Mutation;
                }
            }
            else if (
                component.Writer is IStateWriteBatchParticipant<TFragment> participant
                && participant.TryCreateBatchWrite(
                    componentRequest,
                    out var synchronousResourceId,
                    out componentBatchWriter,
                    out componentMutation
                )
            )
            {
                componentParticipantResourceId = synchronousResourceId;
            }

            if (componentParticipantResourceId is { } componentResolvedResourceId)
            {
                if (
                    componentResourceId is { } declaredResourceId
                    && declaredResourceId != componentResolvedResourceId
                )
                {
                    throw new InvalidOperationException(
                        $"State source '{component.Id}' declares resource '{declaredResourceId}' but its writer targets '{componentResolvedResourceId}'."
                    );
                }

                if (
                    componentBatchWriter is IResourceIdentity batchIdentity
                    && batchIdentity.ResourceId != componentResolvedResourceId
                )
                {
                    throw new InvalidOperationException(
                        $"State source '{component.Id}' prepares a mutation for '{componentResolvedResourceId}' but its batch writer targets '{batchIdentity.ResourceId}'."
                    );
                }

                componentResourceId = componentResolvedResourceId;
            }

            writePlans.Add(
                (
                    component,
                    component.Writer!,
                    componentRequest,
                    componentResourceId,
                    componentBatchWriter,
                    componentMutation
                )
            );
        }

        foreach (var component in composite.Components)
        {
            if (componentOverrides.ContainsKey(component.Id))
            {
                continue;
            }

            var componentState = (
                await component.Reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ).FromSource(component.Id, component.PhysicalOrigin);
            if (componentState.Status != StateReadStatus.Success || componentState.Value is null)
            {
                continue;
            }

            var componentValue = componentState.Value;
            if (componentState.Schema is { } componentSchema)
            {
                componentValue = await MigrateAsync(
                        componentValue,
                        componentSchema,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            componentOverrides.Add(component.Id, componentValue);
        }

        var composed = await composite
            .ReadWithOverridesAsync(componentOverrides, cancellationToken)
            .ConfigureAwait(false);
        if (composed.Status != StateReadStatus.Success || composed.Value is null)
        {
            throw new InvalidOperationException(
                $"Patched composite source '{source.Id}' could not be resolved: {composed.Status}."
            );
        }

        replacements.Add(
            source.Id,
            StateReadResult<TFragment>.Success(
                composed.Value,
                current.Revision,
                modelSchema.ToMetadata()
            ) with
            {
                Revisions = composed.Revisions,
            }
        );
        return;
    }
}
