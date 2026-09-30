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
                $"State source '{target.Id}' has been retired from this state instance."
            );
        }

        _logger?.LogInformation(
            MigrationEvent,
            "Migrating configuration contribution from source {SourceId} to {TargetSourceId} for {ModelType} state {StateName}.",
            source.Id,
            target.Id,
            typeof(TModel).FullName,
            _stateName
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

        var currentSchema = ModelSchema.ToMetadata();
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
                    Condition: RevisionCondition.FromRevision(targetResult.Revision)
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
            "Migrated configuration contribution from source {SourceId} to {TargetSourceId} for {ModelType} state {StateName}.",
            source.Id,
            target.Id,
            typeof(TModel).FullName,
            _stateName
        );
        return migrationResult;
    }

    /// <inheritdoc />
    public ValueTask<StateSourceMigrationResult> MigrateSourceAsync(
        SourceKey<TModel> sourceKey,
        SourceKey<TModel> targetKey,
        CancellationToken cancellationToken = default
    )
    {
        ValidateSourceKey(sourceKey, nameof(sourceKey));
        ValidateSourceKey(targetKey, nameof(targetKey));
        return MigrateSourceAsync(sourceKey.Id, targetKey.Id, cancellationToken);
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
            "Starting storage migration for {ModelType} state {StateName} from sources {SourceIds} to targets {TargetSourceIds}.",
            typeof(TModel).FullName,
            _stateName,
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
                StateReadStatus.NotFound => EmptyFragment,
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

        var merged = EmptyFragment;
        for (var index = sourceContributions.Count - 1; index >= 0; index--)
        {
            merged = merged.Merge(sourceContributions[index].Fragment);
        }

        var currentSchema = ModelSchema.ToMetadata();
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
                    StateReadStatus.NotFound => EmptyFragment,
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
            ISourceWriter<TFragment> Writer,
            TFragment Desired
        )>(targetProjections.Count);
        foreach (var (targetId, project) in targetProjections)
        {
            var target = FindSource(targetId);
            if (!IsSourceActive(target.Id))
            {
                throw new InvalidOperationException(
                    $"State source '{target.Id}' has been retired from this state instance."
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
                StateReadStatus.NotFound => EmptyFragment,
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
                    "Storage migration target {TargetSourceId} already contains the verified contribution for {ModelType} state {StateName}.",
                    target.Id,
                    typeof(TModel).FullName,
                    _stateName
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
                        Condition: RevisionCondition.FromRevision(current.Revision)
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
                "Verified storage migration target {TargetSourceId} for {ModelType} state {StateName} from sources {SourceIds}.",
                target.Id,
                typeof(TModel).FullName,
                _stateName,
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
                "Retired migrated sources {SourceIds} from {ModelType} state {StateName}.",
                string.Join(",", retiredSourceIds),
                typeof(TModel).FullName,
                _stateName
            );
        }

        _logger?.LogInformation(
            MigrationEvent,
            "Completed storage migration for {ModelType} state {StateName} from sources {SourceIds} to targets {TargetSourceIds}; retired {RetiredSourceIds}.",
            typeof(TModel).FullName,
            _stateName,
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

    /// <inheritdoc />
    public ValueTask<StateStorageMigrationResult> MigrateSourcesToTargetsAsync(
        IEnumerable<SourceKey<TModel>> sourceKeys,
        IReadOnlyDictionary<
            SourceKey<TModel>,
            Func<IConfiglueFragment, IConfiglueFragment>
        > targetProjections,
        CancellationToken cancellationToken = default,
        bool retireSources = false
    )
    {
        ArgumentNullException.ThrowIfNull(sourceKeys);
        ArgumentNullException.ThrowIfNull(targetProjections);
        var sourceIds = sourceKeys.Select(key =>
        {
            ValidateSourceKey(key, nameof(sourceKeys));
            return key.Id;
        });
        var stringProjections = targetProjections.ToDictionary(
            pair =>
            {
                ValidateSourceKey(pair.Key, nameof(targetProjections));
                ArgumentNullException.ThrowIfNull(pair.Value);
                return pair.Key.Id;
            },
            static pair => pair.Value,
            StringComparer.Ordinal
        );

        return MigrateSourcesToTargetsAsync(
            sourceIds,
            stringProjections,
            cancellationToken,
            retireSources
        );
    }

    private static void ValidateSourceKey(SourceKey<TModel> sourceKey, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(sourceKey.Id))
        {
            throw new ArgumentException("The source key is uninitialized.", parameterName);
        }
    }
}
