using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;

namespace Configlue;

public sealed partial class ConfiglueOptions<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    /// <inheritdoc />
    public async ValueTask<StateWriteResult> SaveAsync(
        IConfigluePatch patch,
        CancellationToken cancellationToken = default
    )
    {
        using var operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(patch);
        cancellationToken.ThrowIfCancellationRequested();
        var modelSchema = TModel.ConfiglueSchema;
        if (
            patch.Schema.ModelType != typeof(TModel)
            || patch.Schema.Id != modelSchema.Id
            || patch.Schema.Version != modelSchema.Version
        )
        {
            throw new ArgumentException(
                $"The patch schema '{patch.Schema.Id}' does not match '{modelSchema.Id}'.",
                nameof(patch)
            );
        }

        if (patch.IsEmpty)
        {
            var emptyPatchSource = SelectWriteSource(allowPriorityFallback: true);
            var current = await emptyPatchSource
                .Reader.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (current.Status == StateReadStatus.Unavailable)
            {
                throw new InvalidOperationException(
                    $"Cannot safely patch configuration because source '{emptyPatchSource.Id}' is unavailable."
                );
            }

            return new StateWriteResult(current.Revision);
        }

        var fallbackSource = TrySelectDefaultWriteSource();

        var baseline = await ResolveCoreAsync(null, cancellationToken, captureContributions: true)
            .ConfigureAwait(false);
        if (baseline.Result.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException(
                $"Configuration state could not be read: {baseline.Result.Status}."
            );
        }

        if (
            patch.Apply(TModel.ToFragment(baseline.Result.Value!))
            is not TFragment requestedFragment
        )
        {
            throw new InvalidOperationException(
                "The patch returned an incompatible configuration fragment."
            );
        }

        var expectedResolvedModel = CloneModel(TModel.FromFragment(requestedFragment));

        if (
            _defaultWritePlan.PropertyRoutes.Count > 0
            && !Volatile.Read(ref _defaultWritePlanValidated)
        )
        {
            ValidateWritePlan(_defaultWritePlan);
            Volatile.Write(ref _defaultWritePlanValidated, true);
        }

        IReadOnlyDictionary<string, IConfigluePatch> patchesBySource;
        if (patch is IConfiglueRoutablePatch routablePatch)
        {
            patchesBySource = routablePatch.Route(_defaultWritePlan, fallbackSource?.Id);
        }
        else if (patch is IConfiglueMemberPatch memberPatch)
        {
            var routed = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (var member in modelSchema.Members)
            {
                var selected = memberPatch.SelectMembers([member.Id]);
                if (selected.IsEmpty)
                {
                    continue;
                }

                var targetSourceId = _defaultWritePlan.ResolveSourceIdOrNull(
                    member.Name,
                    fallbackSource?.Id
                );
                if (targetSourceId is null)
                {
                    throw new InvalidOperationException(
                        $"No writable source owns '{member.Name}'. Configure a root write target or an explicit write plan."
                    );
                }
                if (!routed.TryGetValue(targetSourceId, out var memberIds))
                {
                    memberIds = [];
                    routed.Add(targetSourceId, memberIds);
                }

                memberIds.Add(member.Id);
            }

            patchesBySource =
                routed.Count == 0
                    ? new Dictionary<string, IConfigluePatch>(StringComparer.Ordinal)
                    {
                        [
                            fallbackSource?.Id
                                ?? throw new InvalidOperationException(
                                    "No writable source owns this patch. Configure a root write target or an explicit write plan."
                                )
                        ] = patch,
                    }
                    : routed.ToDictionary(
                        static route => route.Key,
                        route => memberPatch.SelectMembers(route.Value.ToArray()),
                        StringComparer.Ordinal
                    );
        }
        else
        {
            patchesBySource = new Dictionary<string, IConfigluePatch>(StringComparer.Ordinal)
            {
                [
                    fallbackSource?.Id
                        ?? throw new InvalidOperationException(
                            "No writable source owns this patch. Configure a root write target or an explicit write plan."
                        )
                ] = patch,
            };
        }

        var sourcePatches = patchesBySource
            .Select(static route => new StateSourcePatch(route.Key, route.Value))
            .ToArray();
        var result = await ApplyPatchesCoreAsync(
                sourcePatches,
                baseline.Result.Revisions,
                expectedResolvedModel,
                cancellationToken,
                baseline
            )
            .ConfigureAwait(false);
        var sourceResult = fallbackSource is null
            ? default
            : result.Sources.FirstOrDefault(route =>
                string.Equals(route.SourceId, fallbackSource.Id, StringComparison.Ordinal)
            );
        var revision = sourceResult.SourceId is null
            ? result.Sources[0].Revision
            : sourceResult.Revision;
        return result.Sources.Count == 1
            ? new StateWriteResult(revision)
            : new StateWriteResult(revision) { MultiWriteResult = result };
    }
}
