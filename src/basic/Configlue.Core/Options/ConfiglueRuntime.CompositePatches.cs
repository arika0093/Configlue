using System.Diagnostics;
using Configlue.CompilerServices;
using Configlue.Sources;

namespace Configlue;

internal sealed partial class ConfiglueRuntime<TModel, TFragment>
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
        Dictionary<SourceId, StateReadResult<TFragment>> replacements,
        List<(
            StateSource<TFragment> Source,
            ISourceWriter<TFragment> Writer,
            StateWriteRequest<TFragment> Request,
            ResourceId? ResourceId,
            IResourceBatchWriter? BatchWriter,
            ResourceWriteMutation? Mutation
        )> writePlans,
        CancellationToken cancellationToken
    )
    {
        if (patchRequest.Patch is not IConfiglueDynamicMemberPatch memberPatch)
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

        var routedPatches = new Dictionary<SourceId, IConfigluePatch>();
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
            var routedMemberIds = new Dictionary<SourceId, List<int>>();
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

        var componentOverrides = new Dictionary<SourceId, TFragment>();
        foreach (var (componentId, componentPatch) in routedPatches)
        {
            var component = composite.Components.First(item => item.Id == componentId);
            var componentCurrent = (
                await ReadSourceAsync(component, cancellationToken).ConfigureAwait(false)
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
                StateReadStatus.NotFound => EmptyFragment,
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
                Condition: RevisionCondition.FromRevision(componentCurrent.Revision)
            );
            var resourceContext = GetResourceContext(component);
            var componentResourceId = GetResourceId(component);
            IResourceBatchWriter? componentBatchWriter = null;
            ResourceWriteMutation? componentMutation = null;
            ResourceId? componentParticipantResourceId = null;
            if (
                component.Writer
                is IAsyncSourceWriteBatchParticipant<TFragment> componentParticipant
            )
            {
                var batchPlan = await componentParticipant
                    .TryCreateBatchWriteAsync(resourceContext, componentRequest, cancellationToken)
                    .ConfigureAwait(false);
                if (batchPlan is { } prepared)
                {
                    componentParticipantResourceId = prepared.ResourceId;
                    componentBatchWriter = prepared.BatchWriter;
                    componentMutation = prepared.Mutation;
                }
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
                    && batchIdentity.TryGetResourceId(
                        resourceContext,
                        out var batchWriterResourceId
                    )
                    && batchWriterResourceId != componentResolvedResourceId
                )
                {
                    throw new InvalidOperationException(
                        $"State source '{component.Id}' prepares a mutation for '{componentResolvedResourceId}' but its batch writer targets '{batchWriterResourceId}'."
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
                await ReadSourceAsync(component, cancellationToken).ConfigureAwait(false)
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
            .ReadWithOverridesAsync(
                componentOverrides,
                GetResourceContext(source),
                cancellationToken
            )
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
