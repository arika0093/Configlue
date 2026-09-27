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
    public async ValueTask<StateSourceMigrationResult> MigrateSourceAsync(
        string sourceId,
        string targetId,
        CancellationToken cancellationToken = default
    )
    {
        using var operation = EnterOperation();
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        cancellationToken.ThrowIfCancellationRequested();

        var source = FindSource(sourceId);
        var target = FindSource(targetId);
        if (target.Writer is null)
        {
            throw new InvalidOperationException(
                $"State source '{target.Id}' does not support writes."
            );
        }

        if (!IsSourceActive(target.Id))
        {
            throw new InvalidOperationException(
                $"State source '{target.Id}' has been retired from this options instance."
            );
        }

        _logger?.LogInformation(
            MigrationEvent,
            "Migrating configuration contribution from source {SourceId} to {TargetSourceId} for {ModelType} options {OptionsName}.",
            source.Id,
            target.Id,
            typeof(TModel).FullName,
            _optionsName
        );
        var sourceResult = await ReadMigrationSourceAsync(source, cancellationToken)
            .ConfigureAwait(false);
        if (sourceResult.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException(
                $"Source '{source.Id}' could not be migrated: {sourceResult.Status}."
            );
        }

        var sourceFragment =
            sourceResult.Value
            ?? throw new InvalidOperationException(
                $"State source '{source.Id}' returned a null configuration fragment."
            );
        if (sourceResult.Schema is { } schema)
        {
            sourceFragment = await MigrateAsync(sourceFragment, schema, cancellationToken)
                .ConfigureAwait(false);
        }

        var currentSchema = TModel.ConfiglueSchema.ToMetadata();
        if (
            ReferenceEquals(source, target)
            && (sourceResult.Schema is null || sourceResult.Schema == currentSchema)
        )
        {
            return new StateSourceMigrationResult(
                source.Id,
                target.Id,
                sourceResult.Revision,
                sourceResult.Revision
            );
        }

        var targetResult = ReferenceEquals(source, target)
            ? sourceResult
            : await ReadMigrationSourceAsync(target, cancellationToken).ConfigureAwait(false);
        if (targetResult.Status == StateReadStatus.Unavailable)
        {
            throw new InvalidOperationException($"Target source '{target.Id}' is unavailable.");
        }

        if (targetResult.Status == StateReadStatus.Success && targetResult.Value is null)
        {
            throw new InvalidOperationException(
                $"State source '{target.Id}' returned a null configuration fragment."
            );
        }

        var write = await WriteStateAsync(
                target,
                target.Writer,
                new StateWriteRequest<TFragment>(
                    sourceFragment,
                    targetResult.Revision,
                    CheckRevision: true
                ),
                "source migration",
                cancellationToken,
                source.Id,
                MigrationEvent
            )
            .ConfigureAwait(false);
        var migrationResult = new StateSourceMigrationResult(
            source.Id,
            target.Id,
            sourceResult.Revision,
            write.Revision
        );
        _logger?.LogInformation(
            MigrationEvent,
            "Migrated configuration contribution from source {SourceId} to {TargetSourceId} for {ModelType} options {OptionsName}.",
            source.Id,
            target.Id,
            typeof(TModel).FullName,
            _optionsName
        );
        return migrationResult;
    }

    /// <summary>
    /// Migrates selected source contributions to one or more projected targets. Successful targets are
    /// re-read and verified; repeating the operation skips targets already holding the requested fragment.
    /// </summary>
    public async ValueTask<StateStorageMigrationResult> MigrateSourcesToTargetsAsync(
        IEnumerable<string> sourceIds,
        IReadOnlyDictionary<string, Func<TFragment, TFragment>> targetProjections,
        CancellationToken cancellationToken = default,
        bool retireSources = false
    )
    {
        using var operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(sourceIds);
        ArgumentNullException.ThrowIfNull(targetProjections);
        cancellationToken.ThrowIfCancellationRequested();

        var requestedSourceIds = sourceIds.ToArray();
        if (requestedSourceIds.Length == 0)
        {
            throw new ArgumentException(
                "At least one source must be selected for migration.",
                nameof(sourceIds)
            );
        }

        if (requestedSourceIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Source IDs cannot be empty.", nameof(sourceIds));
        }

        if (
            requestedSourceIds.Distinct(StringComparer.Ordinal).Count() != requestedSourceIds.Length
        )
        {
            throw new ArgumentException("A source can only be selected once.", nameof(sourceIds));
        }

        if (targetProjections.Count == 0)
        {
            throw new ArgumentException(
                "At least one target projection is required.",
                nameof(targetProjections)
            );
        }

        foreach (var target in targetProjections)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(target.Key);
            ArgumentNullException.ThrowIfNull(target.Value);
        }

        object? resolvedBeforeMigration = null;
        if (retireSources)
        {
            var before = await ResolveCoreAsync(null, cancellationToken).ConfigureAwait(false);
            if (before.Result.Status != StateReadStatus.Success)
            {
                throw new InvalidOperationException(
                    $"Configuration state could not be read before source retirement: {before.Result.Status}."
                );
            }

            resolvedBeforeMigration = before.Result.Value;
        }

        var selectedIds = requestedSourceIds.ToHashSet(StringComparer.Ordinal);
        var overlappingTarget = targetProjections.Keys.FirstOrDefault(selectedIds.Contains);
        if (overlappingTarget is not null)
        {
            throw new ArgumentException(
                $"Target '{overlappingTarget}' is also a selected source. Use MigrateSourceAsync for an in-place source migration.",
                nameof(targetProjections)
            );
        }

        var sourceContributions =
            new List<(
                StateSource<TFragment> Source,
                StateReadResult<TFragment> Result,
                TFragment Fragment
            )>();
        var sourceRevisions = new List<StateRevision>();
        _logger?.LogInformation(
            MigrationEvent,
            "Starting storage migration for {ModelType} options {OptionsName} from sources {SourceIds} to targets {TargetSourceIds}.",
            typeof(TModel).FullName,
            _optionsName,
            string.Join(",", requestedSourceIds),
            string.Join(",", targetProjections.Keys)
        );
        foreach (var source in _sourceSet.Sources.Where(source => selectedIds.Contains(source.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ReadMigrationSourceAsync(source, cancellationToken)
                .ConfigureAwait(false);
            sourceRevisions.Add(new StateRevision(source.Id, result.Revision));
            if (result.Status == StateReadStatus.Unavailable)
            {
                throw new InvalidOperationException(
                    $"Source '{source.Id}' could not be migrated because it is unavailable."
                );
            }

            var fragment = result.Status switch
            {
                StateReadStatus.NotFound => TFragment.Empty,
                StateReadStatus.Success => result.Value
                    ?? throw new InvalidOperationException(
                        $"State source '{source.Id}' returned a null configuration fragment."
                    ),
                _ => throw new InvalidOperationException(
                    $"Source '{source.Id}' could not be migrated: {result.Status}."
                ),
            };
            if (result.Schema is { } schema)
            {
                fragment = await MigrateAsync(fragment, schema, cancellationToken)
                    .ConfigureAwait(false);
            }

            sourceContributions.Add((source, result, fragment));
        }

        if (sourceContributions.Count != requestedSourceIds.Length)
        {
            var resolvedIds = sourceContributions
                .Select(static contribution => contribution.Source.Id)
                .ToHashSet(StringComparer.Ordinal);
            var missingId = requestedSourceIds.First(id => !resolvedIds.Contains(id));
            throw new InvalidOperationException($"State source '{missingId}' is not registered.");
        }

        var merged = TFragment.Empty;
        for (var index = sourceContributions.Count - 1; index >= 0; index--)
        {
            merged = merged.Merge(sourceContributions[index].Fragment);
        }

        var currentSchema = TModel.ConfiglueSchema.ToMetadata();
        async ValueTask VerifySourceSnapshotsAsync()
        {
            for (var index = 0; index < sourceContributions.Count; index++)
            {
                var contribution = sourceContributions[index];
                var latest = await ReadMigrationSourceAsync(contribution.Source, cancellationToken)
                    .ConfigureAwait(false);
                if (
                    latest.Status == contribution.Result.Status
                    && latest.Schema == contribution.Result.Schema
                    && string.Equals(
                        latest.Revision,
                        contribution.Result.Revision,
                        StringComparison.Ordinal
                    )
                )
                {
                    continue;
                }

                if (
                    latest.Status != contribution.Result.Status
                    || latest.Schema != contribution.Result.Schema
                )
                {
                    throw LogConflict(
                        $"Source '{contribution.Source.Id}' changed while the storage migration was running."
                    );
                }

                var latestFragment = latest.Status switch
                {
                    StateReadStatus.NotFound => TFragment.Empty,
                    StateReadStatus.Success => latest.Value
                        ?? throw new InvalidOperationException(
                            $"State source '{contribution.Source.Id}' returned a null configuration fragment."
                        ),
                    _ => throw LogConflict(
                        $"Source '{contribution.Source.Id}' became unavailable during migration."
                    ),
                };
                if (latest.Schema is { } latestSchema)
                {
                    latestFragment = await MigrateAsync(
                            latestFragment,
                            latestSchema,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                }

                if (!ConfiglueFragmentComparer.AreEqual(latestFragment, contribution.Fragment))
                {
                    throw LogConflict(
                        $"Source '{contribution.Source.Id}' changed while the storage migration was running."
                    );
                }

                sourceContributions[index] = (contribution.Source, latest, latestFragment);
                sourceRevisions[index] = new StateRevision(contribution.Source.Id, latest.Revision);
            }
        }

        var targetPlans = new List<(
            StateSource<TFragment> Target,
            IStateWriter<TFragment> Writer,
            TFragment Desired
        )>(targetProjections.Count);
        foreach (var (targetId, project) in targetProjections)
        {
            var target = FindSource(targetId);
            if (!IsSourceActive(target.Id))
            {
                throw new InvalidOperationException(
                    $"State source '{target.Id}' has been retired from this options instance."
                );
            }

            if (target.Writer is null)
            {
                throw new InvalidOperationException(
                    $"State source '{target.Id}' does not support writes."
                );
            }

            var desired =
                project(merged)
                ?? throw new InvalidOperationException(
                    $"The migration projection for target '{target.Id}' returned null."
                );
            targetPlans.Add((target, target.Writer, desired));
        }

        var targetResults = new List<StateStorageMigrationTargetResult>(targetPlans.Count);
        foreach (var (target, writer, desired) in targetPlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await VerifySourceSnapshotsAsync().ConfigureAwait(false);
            var current = await ReadMigrationSourceAsync(target, cancellationToken)
                .ConfigureAwait(false);
            if (current.Status == StateReadStatus.Unavailable)
            {
                throw new InvalidOperationException($"Target source '{target.Id}' is unavailable.");
            }

            var currentFragment = current.Status switch
            {
                StateReadStatus.NotFound => TFragment.Empty,
                StateReadStatus.Success => current.Value
                    ?? throw new InvalidOperationException(
                        $"State source '{target.Id}' returned a null configuration fragment."
                    ),
                _ => throw new InvalidOperationException(
                    $"Target source '{target.Id}' could not be read: {current.Status}."
                ),
            };
            if (current.Schema is { } targetSchema)
            {
                currentFragment = await MigrateAsync(
                        currentFragment,
                        targetSchema,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            var targetSchemaIsCurrent = current.Schema is null || current.Schema == currentSchema;
            var targetIsAlreadyCurrent =
                current.Status == StateReadStatus.Success
                || (current.Status == StateReadStatus.NotFound && desired.IsEmpty);
            if (
                targetIsAlreadyCurrent
                && targetSchemaIsCurrent
                && ConfiglueFragmentComparer.AreEqual(currentFragment, desired)
            )
            {
                var confirmation = await ReadMigrationSourceAsync(target, cancellationToken)
                    .ConfigureAwait(false);
                if (
                    confirmation.Status != current.Status
                    || !string.Equals(
                        confirmation.Revision,
                        current.Revision,
                        StringComparison.Ordinal
                    )
                )
                {
                    throw LogConflict(
                        $"Target source '{target.Id}' changed during migration verification."
                    );
                }

                if (confirmation.Status == StateReadStatus.Success)
                {
                    var confirmedFragment =
                        confirmation.Value
                        ?? throw new InvalidOperationException(
                            $"State source '{target.Id}' returned a null configuration fragment."
                        );
                    if (confirmation.Schema is { } confirmationSchema)
                    {
                        confirmedFragment = await MigrateAsync(
                                confirmedFragment,
                                confirmationSchema,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                    }

                    if (
                        (
                            confirmation.Schema is { } confirmedSchema
                            && confirmedSchema != currentSchema
                        ) || !ConfiglueFragmentComparer.AreEqual(confirmedFragment, desired)
                    )
                    {
                        throw LogConflict(
                            $"Target source '{target.Id}' changed during migration verification."
                        );
                    }
                }

                await VerifySourceSnapshotsAsync().ConfigureAwait(false);
                _logger?.LogInformation(
                    MigrationEvent,
                    "Storage migration target {TargetSourceId} already contains the verified contribution for {ModelType} options {OptionsName}.",
                    target.Id,
                    typeof(TModel).FullName,
                    _optionsName
                );
                targetResults.Add(
                    new StateStorageMigrationTargetResult(
                        target.Id,
                        current.Revision,
                        current.Revision,
                        WasAlreadyCurrent: true
                    )
                );
                continue;
            }

            await VerifySourceSnapshotsAsync().ConfigureAwait(false);
            var write = await WriteStateAsync(
                    target,
                    writer,
                    new StateWriteRequest<TFragment>(
                        desired,
                        current.Revision,
                        CheckRevision: true
                    ),
                    "storage migration",
                    cancellationToken,
                    string.Join(",", sourceContributions.Select(static item => item.Source.Id)),
                    MigrationEvent
                )
                .ConfigureAwait(false);
            var verification = await ReadMigrationSourceAsync(target, cancellationToken)
                .ConfigureAwait(false);
            if (
                verification.Status != StateReadStatus.Success
                || !string.Equals(verification.Revision, write.Revision, StringComparison.Ordinal)
            )
            {
                throw LogConflict(
                    $"Target source '{target.Id}' changed before migration verification completed."
                );
            }

            var verifiedFragment =
                verification.Value
                ?? throw new InvalidOperationException(
                    $"State source '{target.Id}' returned a null configuration fragment after migration."
                );
            if (verification.Schema is { } verificationSchema)
            {
                verifiedFragment = await MigrateAsync(
                        verifiedFragment,
                        verificationSchema,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            if (
                (verification.Schema is { } actualSchema && actualSchema != currentSchema)
                || !ConfiglueFragmentComparer.AreEqual(verifiedFragment, desired)
            )
            {
                throw LogConflict(
                    $"Target source '{target.Id}' did not retain the migrated fragment."
                );
            }

            _logger?.LogInformation(
                MigrationEvent,
                "Verified storage migration target {TargetSourceId} for {ModelType} options {OptionsName} from sources {SourceIds}.",
                target.Id,
                typeof(TModel).FullName,
                _optionsName,
                string.Join(",", sourceContributions.Select(static item => item.Source.Id))
            );

            targetResults.Add(
                new StateStorageMigrationTargetResult(
                    target.Id,
                    current.Revision,
                    write.Revision,
                    WasAlreadyCurrent: false
                )
            );
        }

        string[] retiredSourceIds = [];
        if (retireSources)
        {
            await VerifySourceSnapshotsAsync().ConfigureAwait(false);
            await VerifyRetirementPreservesResolvedModelAsync(
                    resolvedBeforeMigration!,
                    sourceContributions,
                    targetPlans,
                    targetResults,
                    cancellationToken
                )
                .ConfigureAwait(false);
            await VerifySourceSnapshotsAsync().ConfigureAwait(false);
            retiredSourceIds = sourceContributions
                .Select(static contribution => contribution.Source.Id)
                .ToArray();
            RetireSourcesFromOptions(retiredSourceIds);
            _logger?.LogInformation(
                MigrationEvent,
                "Retired migrated sources {SourceIds} from {ModelType} options {OptionsName}.",
                string.Join(",", retiredSourceIds),
                typeof(TModel).FullName,
                _optionsName
            );
        }

        _logger?.LogInformation(
            MigrationEvent,
            "Completed storage migration for {ModelType} options {OptionsName} from sources {SourceIds} to targets {TargetSourceIds}; retired {RetiredSourceIds}.",
            typeof(TModel).FullName,
            _optionsName,
            string.Join(",", sourceContributions.Select(static item => item.Source.Id)),
            string.Join(",", targetPlans.Select(static item => item.Target.Id)),
            string.Join(",", retiredSourceIds)
        );

        return new StateStorageMigrationResult(
            sourceContributions.Select(static contribution => contribution.Source.Id),
            new StateRevisionVector(sourceRevisions),
            targetResults,
            retiredSourceIds
        );
    }

    /// <inheritdoc />
    public ValueTask<StateStorageMigrationResult> MigrateSourcesToTargetsAsync(
        IEnumerable<string> sourceIds,
        IReadOnlyDictionary<string, Func<IConfiglueFragment, IConfiglueFragment>> targetProjections,
        CancellationToken cancellationToken = default,
        bool retireSources = false
    )
    {
        ArgumentNullException.ThrowIfNull(targetProjections);
        foreach (var target in targetProjections)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(target.Key);
            ArgumentNullException.ThrowIfNull(target.Value);
        }

        var typedProjections = targetProjections.ToDictionary(
            static pair => pair.Key,
            static pair =>
                (Func<TFragment, TFragment>)(
                    fragment =>
                        pair.Value(fragment) is TFragment projected
                            ? projected
                            : throw new InvalidOperationException(
                                $"The migration projection for target '{pair.Key}' returned an incompatible fragment."
                            )
                ),
            StringComparer.Ordinal
        );
        return MigrateSourcesToTargetsAsync(
            sourceIds,
            typedProjections,
            cancellationToken,
            retireSources
        );
    }

    private async ValueTask VerifyRetirementPreservesResolvedModelAsync(
        object baselineModel,
        IReadOnlyList<(
            StateSource<TFragment> Source,
            StateReadResult<TFragment> Result,
            TFragment Fragment
        )> sourceContributions,
        IReadOnlyList<(
            StateSource<TFragment> Target,
            IStateWriter<TFragment> Writer,
            TFragment Desired
        )> targetPlans,
        IReadOnlyList<StateStorageMigrationTargetResult> targetResults,
        CancellationToken cancellationToken
    )
    {
        var currentSchema = TModel.ConfiglueSchema.ToMetadata();
        var replacements = new Dictionary<string, StateReadResult<TFragment>>(
            StringComparer.Ordinal
        );
        foreach (var (source, result, _) in sourceContributions)
        {
            replacements.Add(
                source.Id,
                StateReadResult<TFragment>
                    .Success(TFragment.Empty, result.Revision, currentSchema)
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
                await target.Reader.ReadAsync(cancellationToken).ConfigureAwait(false)
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
                StateReadStatus.NotFound when desired.IsEmpty => TFragment.Empty,
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
            || !TModel.Diff(before, proposed.Result.Value!).IsEmpty
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
                await target.Reader.ReadAsync(cancellationToken).ConfigureAwait(false)
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
