using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Configlue.CompilerServices;
using Configlue.Sources;
using Microsoft.Extensions.Logging;

namespace Configlue;

internal sealed partial class ConfiglueRuntime<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private async ValueTask VerifyRetirementPreservesResolvedModelAsync(
        object baselineModel,
        IReadOnlyList<(
            StateSource<TFragment> Source,
            StateReadResult<TFragment> Result,
            TFragment Fragment
        )> sourceContributions,
        IReadOnlyList<(
            StateSource<TFragment> Target,
            ISourceWriter<TFragment> Writer,
            TFragment Desired
        )> targetPlans,
        IReadOnlyList<StateStorageMigrationTargetResult> targetResults,
        CancellationToken cancellationToken
    )
    {
        var currentSchema = ModelSchema.ToMetadata();
        var replacements = new Dictionary<string, StateReadResult<TFragment>>(
            StringComparer.Ordinal
        );
        foreach (var (source, result, _) in sourceContributions)
        {
            replacements.Add(
                source.Id,
                StateReadResult<TFragment>
                    .Success(EmptyFragment, result.Revision, currentSchema)
                    .FromSource(source.Id, source.PhysicalOrigin)
            );
        }

        foreach (var (target, _, desired) in targetPlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = targetResults.First(result =>
                string.Equals(result.TargetId, target.Id, StringComparison.Ordinal)
            );
            var current = (
                await ReadSourceAsync(target, cancellationToken).ConfigureAwait(false)
            ).FromSource(target.Id, target.PhysicalOrigin);
            if (
                current.Status == StateReadStatus.Unavailable
                || !string.Equals(
                    current.Revision,
                    outcome.TargetRevision,
                    StringComparison.Ordinal
                )
            )
            {
                throw LogConflict($"Target source '{target.Id}' changed before source retirement.");
            }

            var currentFragment = current.Status switch
            {
                StateReadStatus.NotFound when desired.IsEmpty => EmptyFragment,
                StateReadStatus.Success => current.Value
                    ?? throw new InvalidOperationException(
                        $"State source '{target.Id}' returned a null configuration fragment."
                    ),
                _ => throw LogConflict(
                    $"Target source '{target.Id}' is not available for source retirement."
                ),
            };
            if (current.Schema is { } schema)
            {
                currentFragment = await MigrateAsync(currentFragment, schema, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (
                (current.Schema is { } actualSchema && actualSchema != currentSchema)
                || !ConfiglueFragmentComparer.AreEqual(currentFragment, desired)
            )
            {
                throw LogConflict(
                    $"Target source '{target.Id}' no longer contains the verified migration result."
                );
            }

            replacements.Add(
                target.Id,
                StateReadResult<TFragment>
                    .Success(desired, current.Revision, currentSchema)
                    .FromSource(target.Id, target.PhysicalOrigin)
            );
        }

        var proposed = await ResolveCoreAsync(replacements, cancellationToken)
            .ConfigureAwait(false);
        if (
            proposed.Result.Status != StateReadStatus.Success
            || baselineModel is not TModel before
            || !Diff(before, proposed.Result.Value!).IsEmpty
        )
        {
            throw LogConflict(
                "The migrated targets cannot replace the selected sources without changing the effective configuration."
            );
        }

        Validate(proposed.Result.Value!);
        foreach (var (target, _, desired) in targetPlans)
        {
            var outcome = targetResults.First(result =>
                string.Equals(result.TargetId, target.Id, StringComparison.Ordinal)
            );
            var latest = (
                await ReadSourceAsync(target, cancellationToken).ConfigureAwait(false)
            ).FromSource(target.Id, target.PhysicalOrigin);
            if (!string.Equals(latest.Revision, outcome.TargetRevision, StringComparison.Ordinal))
            {
                throw LogConflict(
                    $"Target source '{target.Id}' changed while source retirement was being verified."
                );
            }

            if (latest.Status == StateReadStatus.NotFound && desired.IsEmpty)
            {
                continue;
            }

            if (latest.Status != StateReadStatus.Success || latest.Value is null)
            {
                throw LogConflict(
                    $"Target source '{target.Id}' is not available for source retirement."
                );
            }

            var latestFragment = latest.Schema is { } latestSchema
                ? await MigrateAsync(latest.Value, latestSchema, cancellationToken)
                    .ConfigureAwait(false)
                : latest.Value;
            if (
                (latest.Schema is { } actualSchema && actualSchema != currentSchema)
                || !ConfiglueFragmentComparer.AreEqual(latestFragment, desired)
            )
            {
                throw LogConflict(
                    $"Target source '{target.Id}' no longer contains the verified migration result."
                );
            }
        }
    }
}
