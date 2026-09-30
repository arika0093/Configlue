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
    public async ValueTask<StateWriteReceipt> SaveAsync(
        IConfigluePatch patch,
        CancellationToken cancellationToken = default
    )
    {
        using var operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(patch);
        cancellationToken.ThrowIfCancellationRequested();
        var modelSchema = ModelSchema;
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
            var current = await ReadSourceAsync(emptyPatchSource, cancellationToken)
                .ConfigureAwait(false);
            if (current.Status == StateReadStatus.Unavailable)
            {
                throw new InvalidOperationException(
                    $"Cannot safely patch configuration because source '{emptyPatchSource.Id}' is unavailable."
                );
            }

            return StateWriteReceipt.Empty;
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

        var baselineFragment = baseline.MergedFragment ?? ToFragment(baseline.Result.Value!);
        if (patch.Apply(baselineFragment) is not TFragment requestedFragment)
        {
            throw new InvalidOperationException(
                "The patch returned an incompatible configuration fragment."
            );
        }

        var expectedResolvedModel = CloneModel(FromFragment(requestedFragment));

        if (_defaultWritePlan.PropertyRoutes.Count > 0)
        {
            ValidateWritePlan(_defaultWritePlan);
        }

        IReadOnlyDictionary<string, IConfigluePatch> patchesBySource;
        if (_defaultWritePlan.PropertyRoutes.Count == 0 && fallbackSource is not null)
        {
            // With a single default writable source every member routes to it, so the
            // per-member routing work can be skipped entirely.
            patchesBySource = new Dictionary<string, IConfigluePatch>(StringComparer.Ordinal)
            {
                [fallbackSource.Id] = patch,
            };
        }
        else if (patch is IConfiglueRoutablePatch routablePatch)
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
                    ConfiglueMemberPath.Root(modelSchema).Append(member.Id),
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
        return result;
    }
}
