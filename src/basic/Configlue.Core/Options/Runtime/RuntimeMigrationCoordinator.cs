using Configlue.CompilerServices;
using Configlue.Sources;

namespace Configlue;

/// <summary>
/// Owns schema and storage migration for one runtime.
///
/// Fragment migration runs through the resolution engine's diagnostics-wrapped
/// path; storage migration (source-to-source and source-to-targets copies with
/// revision verification and optional retirement) is orchestrated here over the
/// topology, write coordinator, and validation pipeline. Narrow resolution
/// snapshots are passed explicitly instead of sharing mutable resolve state.
/// </summary>
internal sealed class RuntimeMigrationCoordinator<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly RuntimeResolutionEngine<TModel, TFragment> _engine;
    private readonly RuntimeSourceTopology<TFragment> _topology;
    private readonly RuntimeWriteCoordinator<TModel, TFragment> _writes;
    private readonly RuntimeValidationPipeline<TModel, TFragment> _validation;
    private readonly RuntimeDiagnosticRecorder _diagnostics;
    private readonly RuntimeLifetime _lifetime;
    private readonly StateSource<TFragment>[] _migrationSources;
    private readonly Dictionary<SourceId, StateSource<TFragment>> _migrationSourceById;

    internal RuntimeMigrationCoordinator(
        RuntimeResolutionEngine<TModel, TFragment> engine,
        RuntimeSourceTopology<TFragment> topology,
        RuntimeWriteCoordinator<TModel, TFragment> writes,
        RuntimeValidationPipeline<TModel, TFragment> validation,
        RuntimeDiagnosticRecorder diagnostics,
        RuntimeLifetime lifetime,
        IEnumerable<StateSource<TFragment>>? migrationSources = null
    )
    {
        _engine = engine;
        _topology = topology;
        _writes = writes;
        _validation = validation;
        _diagnostics = diagnostics;
        _lifetime = lifetime;
        var declared = migrationSources?.ToArray() ?? [];
        if (declared.Any(static source => source is null))
        {
            throw new ArgumentException(
                "A migration-only source collection cannot contain null sources.",
                nameof(migrationSources)
            );
        }

        var duplicate = declared
            .GroupBy(static source => source.Id)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Migration-only source id '{duplicate.Key}' is registered more than once.",
                nameof(migrationSources)
            );
        }

        var colliding = declared.FirstOrDefault(source =>
            topology.SourceSet.Sources.Any(candidate => candidate.Id == source.Id)
        );
        if (colliding is not null)
        {
            throw new ArgumentException(
                $"Migration-only source '{colliding.Id}' is already registered as an active runtime source.",
                nameof(migrationSources)
            );
        }

        _migrationSources = declared;
        _migrationSourceById = declared.ToDictionary(
            static source => source.Id,
            static source => source
        );
    }

    /// <summary>Migration-only sources, invisible to normal resolution.</summary>
    internal IReadOnlyList<StateSource<TFragment>> MigrationSources => _migrationSources;

    /// <summary>
    /// Resolves a migration participant from the active topology first and from the
    /// migration-only definitions second.
    /// </summary>
    internal StateSource<TFragment> FindMigrationSource(SourceId sourceId)
    {
        var active = _topology.SourceSet.Sources.FirstOrDefault(candidate =>
            candidate.Id == sourceId
        );
        if (active is not null)
        {
            return active;
        }

        if (_migrationSourceById.TryGetValue(sourceId, out var migrationOnly))
        {
            return migrationOnly;
        }

        throw new InvalidOperationException($"State source '{sourceId}' is not registered.");
    }

    internal bool IsMigrationOnlySource(SourceId sourceId) =>
        _migrationSourceById.ContainsKey(sourceId);

    internal async ValueTask<StateSourceMigrationResult> MigrateSourceAsync(
        SourceId sourceId,
        SourceId targetId,
        CancellationToken cancellationToken = default
    )
    {
        var diagnostic = _diagnostics.Start(
            ConfiglueDiagnosticEventKind.MigrationStarted,
            sourceId
        );
        try
        {
            var result = await MigrateSourceImplementationAsync(
                    sourceId,
                    targetId,
                    cancellationToken
                )
                .ConfigureAwait(false);
            diagnostic.Complete(
                ConfiglueDiagnosticEventKind.MigrationCompleted,
                hasRevision: result.TargetRevision is not null
            );
            return result;
        }
        catch (Exception exception)
        {
            diagnostic.Fail(
                ConfiglueDiagnosticEventKind.MigrationFailed,
                exception,
                exception is OperationCanceledException && cancellationToken.IsCancellationRequested
            );
            throw;
        }
    }

    internal ValueTask<StateSourceMigrationResult> MigrateSourceAsync(
        SourceKey<TModel> sourceKey,
        SourceKey<TModel> targetKey,
        CancellationToken cancellationToken = default
    )
    {
        ValidateSourceKey(sourceKey, nameof(sourceKey));
        ValidateSourceKey(targetKey, nameof(targetKey));
        return MigrateSourceAsync(sourceKey.Id, targetKey.Id, cancellationToken);
    }

    private async ValueTask<StateSourceMigrationResult> MigrateSourceImplementationAsync(
        SourceId sourceId,
        SourceId targetId,
        CancellationToken cancellationToken
    )
    {
        using var operation = _lifetime.EnterOperation();
        if (sourceId.IsDefault || targetId.IsDefault)
        {
            throw new ArgumentException("Source IDs must be non-empty.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var source = FindMigrationSource(sourceId);
        var target = FindMigrationSource(targetId);
        if (target.Writer is null)
        {
            throw new InvalidOperationException(
                $"State source '{target.Id}' does not support writes."
            );
        }

        if (!IsMigrationOnlySource(target.Id) && !_topology.IsSourceActive(target.Id))
        {
            throw new InvalidOperationException(
                $"State source '{target.Id}' has been retired from this state instance."
            );
        }

        var sourceResult = await _engine
            .ReadMigrationSourceAsync(source, cancellationToken)
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
            sourceFragment = await _engine
                .MigrateFragmentAsync(sourceFragment, schema, cancellationToken)
                .ConfigureAwait(false);
        }

        var currentSchema = RuntimeModel<TModel, TFragment>.Schema.ToMetadata();
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

        StateReadResult<TFragment> targetResult;
        var isSamePhysicalResource =
            !ReferenceEquals(source, target) && SharesPhysicalResource(source, target);
        if (ReferenceEquals(source, target))
        {
            targetResult = sourceResult;
        }
        else if (isSamePhysicalResource)
        {
            targetResult = default;
        }
        else
        {
            targetResult = await _engine
                .ReadMigrationSourceAsync(target, cancellationToken)
                .ConfigureAwait(false);
        }

        if (isSamePhysicalResource)
        {
            // Same physical resource, different codecs (in-place representation replacement):
            // the target reader would classify the old bytes as invalid payload, so the
            // conditional replace is based on the original physical revision captured through
            // the old codec instead of a target pre-read.
            var sameResourceWrite = await WriteVerifiedMigrationAsync(
                    target,
                    sourceFragment,
                    sourceResult.Revision,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return new StateSourceMigrationResult(
                source.Id,
                target.Id,
                sourceResult.Revision,
                sameResourceWrite.Revision
            );
        }

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

        var write = await WriteVerifiedMigrationAsync(
                target,
                sourceFragment,
                targetResult.Revision,
                cancellationToken
            )
            .ConfigureAwait(false);
        var migrationResult = new StateSourceMigrationResult(
            source.Id,
            target.Id,
            sourceResult.Revision,
            write.Revision
        );
        return migrationResult;
    }

    /// <summary>
    /// Whether two migration participants address the same physical resource (in-place
    /// representation replacement with old/new codecs over one backing store).
    /// </summary>
    private bool SharesPhysicalResource(
        StateSource<TFragment> source,
        StateSource<TFragment> target
    )
    {
        if (ReferenceEquals(source, target))
        {
            return true;
        }

        var sourceId = _engine.GetResourceId(source);
        var targetId = _engine.GetResourceId(target);
        return sourceId is { IsDefault: false } left
            && targetId is { IsDefault: false } right
            && left == right;
    }

    /// <summary>
    /// Resolves the in-place replacement base for a target that shares its physical resource
    /// with a selected migration source but cannot be pre-read through its own codec.
    /// </summary>
    private TFragment ResolveSameResourceBase(
        StateSource<TFragment> target,
        StateReadStatus targetStatus,
        IReadOnlyList<(
            StateSource<TFragment> Source,
            StateReadResult<TFragment> Result,
            TFragment Fragment
        )> sourceContributions,
        out string? baseRevision
    )
    {
        var match = sourceContributions.FirstOrDefault(contribution =>
            SharesPhysicalResource(contribution.Source, target)
        );
        if (match.Source is not null)
        {
            baseRevision = match.Result.Revision;
            return RuntimeModel<TModel, TFragment>.EmptyFragment;
        }

        throw new InvalidOperationException(
            $"Target source '{target.Id}' could not be read: {targetStatus}."
        );
    }

    /// <summary>
    /// Writes a migrated fragment with revision protection and verifies it by re-reading
    /// through the target codec.
    /// </summary>
    /// <returns>The written target revision.</returns>
    private async ValueTask<StateWriteResult> WriteVerifiedMigrationAsync(
        StateSource<TFragment> target,
        TFragment desiredFragment,
        string? baseRevision,
        CancellationToken cancellationToken
    )
    {
        var currentSchema = RuntimeModel<TModel, TFragment>.Schema.ToMetadata();
        var write = await _writes
            .WriteObservedAsync(
                target,
                target.Writer!,
                new StateWriteRequest<TFragment>(
                    desiredFragment,
                    Condition: RevisionCondition.FromRevision(baseRevision)
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        var verification = await _engine
            .ReadMigrationSourceAsync(target, cancellationToken)
            .ConfigureAwait(false);
        if (
            verification.Status != StateReadStatus.Success
            || !string.Equals(verification.Revision, write.Revision, StringComparison.Ordinal)
        )
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
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
            verifiedFragment = await _engine
                .MigrateFragmentAsync(verifiedFragment, verificationSchema, cancellationToken)
                .ConfigureAwait(false);
        }

        if (
            (verification.Schema is { } actualSchema && actualSchema != currentSchema)
            || !ConfiglueFragmentComparer.AreEqual(verifiedFragment, desiredFragment)
        )
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                $"Target source '{target.Id}' did not retain the migrated fragment."
            );
        }

        return write;
    }

    /// <summary>
    /// Explicit legacy representation adoption: when the canonical target already holds state,
    /// nothing is written and the result is null. Otherwise the first readable legacy
    /// representation is decoded, schema-migrated, written to the canonical target with
    /// revision protection, and verified by re-reading. Migration-only sources never become
    /// active contributions; normal runtime operation continues on the canonical source only.
    /// </summary>
    /// <returns>
    /// The adoption result, or null when the canonical target already holds state or no legacy
    /// representation holds migratable state.
    /// </returns>
    internal async ValueTask<StateSourceMigrationResult?> AdoptLegacyAsync(
        SourceId canonicalTargetId,
        IEnumerable<SourceId> legacySourceIds,
        CancellationToken cancellationToken = default
    )
    {
        using var operation = _lifetime.EnterOperation();
        ArgumentNullException.ThrowIfNull(legacySourceIds);
        if (canonicalTargetId.IsDefault)
        {
            throw new ArgumentException("The canonical target ID must be non-empty.");
        }

        var legacyIds = legacySourceIds.ToArray();
        if (legacyIds.Length == 0 || legacyIds.Any(static id => id.IsDefault))
        {
            throw new ArgumentException(
                "At least one non-empty legacy source ID is required.",
                nameof(legacySourceIds)
            );
        }

        if (legacyIds.Distinct().Count() != legacyIds.Length)
        {
            throw new ArgumentException(
                "A legacy source can only be selected once.",
                nameof(legacySourceIds)
            );
        }

        if (legacyIds.Contains(canonicalTargetId))
        {
            throw new ArgumentException(
                "The canonical target cannot also be a legacy source.",
                nameof(legacySourceIds)
            );
        }

        cancellationToken.ThrowIfCancellationRequested();
        var diagnostic = _diagnostics.Start(
            ConfiglueDiagnosticEventKind.MigrationStarted,
            canonicalTargetId
        );
        try
        {
            var result = await AdoptLegacyImplementationAsync(
                    canonicalTargetId,
                    legacyIds,
                    cancellationToken
                )
                .ConfigureAwait(false);
            diagnostic.Complete(
                ConfiglueDiagnosticEventKind.MigrationCompleted,
                hasRevision: result?.TargetRevision is not null
            );
            return result;
        }
        catch (Exception exception)
        {
            diagnostic.Fail(
                ConfiglueDiagnosticEventKind.MigrationFailed,
                exception,
                exception is OperationCanceledException && cancellationToken.IsCancellationRequested
            );
            throw;
        }
    }

    private async ValueTask<StateSourceMigrationResult?> AdoptLegacyImplementationAsync(
        SourceId canonicalTargetId,
        SourceId[] legacyIds,
        CancellationToken cancellationToken
    )
    {
        var canonical = FindMigrationSource(canonicalTargetId);
        if (canonical.Writer is null)
        {
            throw new InvalidOperationException(
                $"State source '{canonical.Id}' does not support writes."
            );
        }

        if (!IsMigrationOnlySource(canonical.Id) && !_topology.IsSourceActive(canonical.Id))
        {
            throw new InvalidOperationException(
                $"State source '{canonical.Id}' has been retired from this state instance."
            );
        }

        var canonicalResult = await _engine
            .ReadMigrationSourceAsync(canonical, cancellationToken)
            .ConfigureAwait(false);
        if (canonicalResult.Status == StateReadStatus.Unavailable)
        {
            throw new InvalidOperationException($"Target source '{canonical.Id}' is unavailable.");
        }

        if (canonicalResult.Status == StateReadStatus.Success)
        {
            // Canonical state is already present: never overwrite it from legacy input.
            return null;
        }

        foreach (var legacyId in legacyIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var legacy = FindMigrationSource(legacyId);
            var legacyResult = await _engine
                .ReadMigrationSourceAsync(legacy, cancellationToken)
                .ConfigureAwait(false);
            if (legacyResult.Status == StateReadStatus.Unavailable)
            {
                throw new InvalidOperationException(
                    $"Source '{legacy.Id}' could not be migrated because it is unavailable."
                );
            }

            if (legacyResult.Status != StateReadStatus.Success)
            {
                // NotFound or InvalidPayload: probe the next representation.
                continue;
            }

            var fragment =
                legacyResult.Value
                ?? throw new InvalidOperationException(
                    $"State source '{legacy.Id}' returned a null configuration fragment."
                );
            if (legacyResult.Schema is { } schema)
            {
                fragment = await _engine
                    .MigrateFragmentAsync(fragment, schema, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!ReferenceEquals(legacy, canonical) && SharesPhysicalResource(legacy, canonical))
            {
                var sameResourceWrite = await WriteVerifiedMigrationAsync(
                        canonical,
                        fragment,
                        legacyResult.Revision,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return new StateSourceMigrationResult(
                    legacy.Id,
                    canonical.Id,
                    legacyResult.Revision,
                    sameResourceWrite.Revision
                );
            }

            // Re-read the canonical target so a concurrent canonical write wins over legacy input.
            var current = await _engine
                .ReadMigrationSourceAsync(canonical, cancellationToken)
                .ConfigureAwait(false);
            if (current.Status == StateReadStatus.Unavailable)
            {
                throw new InvalidOperationException(
                    $"Target source '{canonical.Id}' is unavailable."
                );
            }

            if (current.Status == StateReadStatus.Success)
            {
                return null;
            }

            var write = await WriteVerifiedMigrationAsync(
                    canonical,
                    fragment,
                    current.Revision,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return new StateSourceMigrationResult(
                legacy.Id,
                canonical.Id,
                legacyResult.Revision,
                write.Revision
            );
        }

        return null;
    }

    internal ValueTask<StateStorageMigrationResult> MigrateSourcesToTargetsAsync(
        IEnumerable<SourceId> sourceIds,
        IReadOnlyDictionary<
            SourceId,
            Func<IConfiglueFragment, IConfiglueFragment>
        > targetProjections,
        CancellationToken cancellationToken = default,
        bool retireSources = false
    )
    {
        ArgumentNullException.ThrowIfNull(sourceIds);
        ArgumentNullException.ThrowIfNull(targetProjections);
        var typedProjections = targetProjections.ToDictionary(
            static projection => projection.Key,
            projection =>
                (Func<TFragment, TFragment>)(
                    fragment =>
                        projection.Value(fragment) is TFragment projected
                            ? projected
                            : throw new InvalidOperationException(
                                $"The migration projection for target '{projection.Key}' returned an incompatible fragment."
                            )
                )
        );
        return MigrateSourcesToTargetsAsync(
            sourceIds,
            typedProjections,
            cancellationToken,
            retireSources
        );
    }

    internal async ValueTask<StateStorageMigrationResult> MigrateSourcesToTargetsAsync(
        IEnumerable<SourceId> sourceIds,
        IReadOnlyDictionary<SourceId, Func<TFragment, TFragment>> targetProjections,
        CancellationToken cancellationToken = default,
        bool retireSources = false
    )
    {
        var diagnostic = _diagnostics.Start(ConfiglueDiagnosticEventKind.MigrationStarted);
        try
        {
            var result = await MigrateSourcesImplementationAsync(
                    sourceIds,
                    targetProjections,
                    cancellationToken,
                    retireSources
                )
                .ConfigureAwait(false);
            diagnostic.Complete(ConfiglueDiagnosticEventKind.MigrationCompleted);
            return result;
        }
        catch (Exception exception)
        {
            diagnostic.Fail(
                ConfiglueDiagnosticEventKind.MigrationFailed,
                exception,
                exception is OperationCanceledException && cancellationToken.IsCancellationRequested
            );
            throw;
        }
    }

    internal ValueTask<StateStorageMigrationResult> MigrateSourcesToTargetsAsync(
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
            static pair => pair.Value
        );

        return MigrateSourcesToTargetsAsync(
            sourceIds,
            stringProjections,
            cancellationToken,
            retireSources
        );
    }

    private async ValueTask<StateStorageMigrationResult> MigrateSourcesImplementationAsync(
        IEnumerable<SourceId> sourceIds,
        IReadOnlyDictionary<SourceId, Func<TFragment, TFragment>> targetProjections,
        CancellationToken cancellationToken,
        bool retireSources
    )
    {
        using var operation = _lifetime.EnterOperation();
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

        if (requestedSourceIds.Any(static sourceId => sourceId.IsDefault))
        {
            throw new ArgumentException("Source IDs cannot be empty.", nameof(sourceIds));
        }

        if (requestedSourceIds.Distinct().Count() != requestedSourceIds.Length)
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
            if (target.Key.IsDefault)
            {
                throw new ArgumentException(
                    "Target source IDs cannot be empty.",
                    nameof(targetProjections)
                );
            }
            ArgumentNullException.ThrowIfNull(target.Value);
        }

        object? resolvedBeforeMigration = null;
        if (retireSources)
        {
            var before = await _engine.ResolveAsync(null, cancellationToken).ConfigureAwait(false);
            if (before.Result.Status != StateReadStatus.Success)
            {
                throw new InvalidOperationException(
                    $"Configuration state could not be read before source retirement: {before.Result.Status}."
                );
            }

            resolvedBeforeMigration = before.Result.Value;
        }

        var selectedIds = requestedSourceIds.ToHashSet();
        var overlappingTarget = targetProjections.Keys.FirstOrDefault(selectedIds.Contains);
        if (!overlappingTarget.IsDefault)
        {
            throw new ArgumentException(
                $"Target '{overlappingTarget}' is also a selected source. Use MigrateSourceAsync for an in-place source migration.",
                nameof(targetProjections)
            );
        }

        if (retireSources)
        {
            var migrationOnlySelected = requestedSourceIds.FirstOrDefault(IsMigrationOnlySource);
            if (!migrationOnlySelected.IsDefault)
            {
                throw new InvalidOperationException(
                    $"Migration-only source '{migrationOnlySelected}' cannot be retired because it is not part of the active runtime topology."
                );
            }
        }

        var orderedSources = requestedSourceIds
            .Select((id, index) => (Source: FindMigrationSource(id), Index: index))
            .OrderByDescending(static item => item.Source.Priority)
            .ThenBy(static item => item.Index)
            .Select(static item => item.Source)
            .ToArray();
        var sourceContributions =
            new List<(
                StateSource<TFragment> Source,
                StateReadResult<TFragment> Result,
                TFragment Fragment
            )>();
        var sourceRevisions = new List<StateRevision>();
        foreach (var source in orderedSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _engine
                .ReadMigrationSourceAsync(source, cancellationToken)
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
                StateReadStatus.NotFound => RuntimeModel<TModel, TFragment>.EmptyFragment,
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
                fragment = await _engine
                    .MigrateFragmentAsync(fragment, schema, cancellationToken)
                    .ConfigureAwait(false);
            }

            sourceContributions.Add((source, result, fragment));
        }

        var merged = RuntimeModel<TModel, TFragment>.EmptyFragment;
        for (var index = sourceContributions.Count - 1; index >= 0; index--)
        {
            merged = merged.Merge(sourceContributions[index].Fragment);
        }

        var currentSchema = RuntimeModel<TModel, TFragment>.Schema.ToMetadata();
        async ValueTask VerifySourceSnapshotsAsync()
        {
            for (var index = 0; index < sourceContributions.Count; index++)
            {
                var contribution = sourceContributions[index];
                var latest = await _engine
                    .ReadMigrationSourceAsync(contribution.Source, cancellationToken)
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
                    throw RuntimeState.NewConflict(
                        _diagnostics,
                        $"Source '{contribution.Source.Id}' changed while the storage migration was running."
                    );
                }

                var latestFragment = latest.Status switch
                {
                    StateReadStatus.NotFound => RuntimeModel<TModel, TFragment>.EmptyFragment,
                    StateReadStatus.Success => latest.Value
                        ?? throw new InvalidOperationException(
                            $"State source '{contribution.Source.Id}' returned a null configuration fragment."
                        ),
                    _ => throw RuntimeState.NewConflict(
                        _diagnostics,
                        $"Source '{contribution.Source.Id}' became unavailable during migration."
                    ),
                };
                if (latest.Schema is { } latestSchema)
                {
                    latestFragment = await _engine
                        .MigrateFragmentAsync(latestFragment, latestSchema, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (!ConfiglueFragmentComparer.AreEqual(latestFragment, contribution.Fragment))
                {
                    throw RuntimeState.NewConflict(
                        _diagnostics,
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
            var target = FindMigrationSource(targetId);
            if (!IsMigrationOnlySource(target.Id) && !_topology.IsSourceActive(target.Id))
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
            var current = await _engine
                .ReadMigrationSourceAsync(target, cancellationToken)
                .ConfigureAwait(false);
            if (current.Status == StateReadStatus.Unavailable)
            {
                throw new InvalidOperationException($"Target source '{target.Id}' is unavailable.");
            }

            string? sameResourceBaseRevision = null;
            var currentFragment = current.Status switch
            {
                StateReadStatus.NotFound => RuntimeModel<TModel, TFragment>.EmptyFragment,
                StateReadStatus.Success => current.Value
                    ?? throw new InvalidOperationException(
                        $"State source '{target.Id}' returned a null configuration fragment."
                    ),
                _ => ResolveSameResourceBase(
                    target,
                    current.Status,
                    sourceContributions,
                    out sameResourceBaseRevision
                ),
            };
            if (
                current.Schema is { } targetSchema
                && current.Status != StateReadStatus.InvalidPayload
            )
            {
                currentFragment = await _engine
                    .MigrateFragmentAsync(currentFragment, targetSchema, cancellationToken)
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
                var confirmation = await _engine
                    .ReadMigrationSourceAsync(target, cancellationToken)
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
                    throw RuntimeState.NewConflict(
                        _diagnostics,
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
                        confirmedFragment = await _engine
                            .MigrateFragmentAsync(
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
                        throw RuntimeState.NewConflict(
                            _diagnostics,
                            $"Target source '{target.Id}' changed during migration verification."
                        );
                    }
                }

                await VerifySourceSnapshotsAsync().ConfigureAwait(false);
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
            var write = await _writes
                .WriteObservedAsync(
                    target,
                    writer,
                    new StateWriteRequest<TFragment>(
                        desired,
                        Condition: RevisionCondition.FromRevision(
                            sameResourceBaseRevision ?? current.Revision
                        )
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
            var verification = await _engine
                .ReadMigrationSourceAsync(target, cancellationToken)
                .ConfigureAwait(false);
            if (
                verification.Status != StateReadStatus.Success
                || !string.Equals(verification.Revision, write.Revision, StringComparison.Ordinal)
            )
            {
                throw RuntimeState.NewConflict(
                    _diagnostics,
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
                verifiedFragment = await _engine
                    .MigrateFragmentAsync(verifiedFragment, verificationSchema, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (
                (verification.Schema is { } actualSchema && actualSchema != currentSchema)
                || !ConfiglueFragmentComparer.AreEqual(verifiedFragment, desired)
            )
            {
                throw RuntimeState.NewConflict(
                    _diagnostics,
                    $"Target source '{target.Id}' did not retain the migrated fragment."
                );
            }

            targetResults.Add(
                new StateStorageMigrationTargetResult(
                    target.Id,
                    current.Revision,
                    write.Revision,
                    WasAlreadyCurrent: false
                )
            );
        }

        SourceId[] retiredSourceIds = [];
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
            var active = _topology.RetireSources(retiredSourceIds);
            if (active is not null)
            {
                _diagnostics.SetActiveSources(active.Select(static source => source.Id));
            }
        }

        return new StateStorageMigrationResult(
            sourceContributions.Select(static contribution => contribution.Source.Id),
            new StateRevisionVector(sourceRevisions),
            targetResults,
            retiredSourceIds
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
            ISourceWriter<TFragment> Writer,
            TFragment Desired
        )> targetPlans,
        IReadOnlyList<StateStorageMigrationTargetResult> targetResults,
        CancellationToken cancellationToken
    )
    {
        var currentSchema = RuntimeModel<TModel, TFragment>.Schema.ToMetadata();
        var replacements = new Dictionary<SourceId, StateReadResult<TFragment>>();
        foreach (var (source, result, _) in sourceContributions)
        {
            replacements.Add(
                source.Id,
                StateReadResult<TFragment>
                    .Success(
                        RuntimeModel<TModel, TFragment>.EmptyFragment,
                        result.Revision,
                        currentSchema
                    )
                    .FromSource(source.Id, source.PhysicalOrigin)
            );
        }

        foreach (var (target, _, desired) in targetPlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = targetResults.First(result => result.TargetId == target.Id);
            var current = (
                await _engine.ReadSourceAsync(target, cancellationToken).ConfigureAwait(false)
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
                throw RuntimeState.NewConflict(
                    _diagnostics,
                    $"Target source '{target.Id}' changed before source retirement."
                );
            }

            var currentFragment = current.Status switch
            {
                StateReadStatus.NotFound when desired.IsEmpty => RuntimeModel<
                    TModel,
                    TFragment
                >.EmptyFragment,
                StateReadStatus.Success => current.Value
                    ?? throw new InvalidOperationException(
                        $"State source '{target.Id}' returned a null configuration fragment."
                    ),
                _ => throw RuntimeState.NewConflict(
                    _diagnostics,
                    $"Target source '{target.Id}' is not available for source retirement."
                ),
            };
            if (current.Schema is { } schema)
            {
                currentFragment = await _engine
                    .MigrateFragmentAsync(currentFragment, schema, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (
                (current.Schema is { } actualSchema && actualSchema != currentSchema)
                || !ConfiglueFragmentComparer.AreEqual(currentFragment, desired)
            )
            {
                throw RuntimeState.NewConflict(
                    _diagnostics,
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

        var proposed = await _engine
            .ResolveAsync(replacements, cancellationToken)
            .ConfigureAwait(false);
        if (
            proposed.Result.Status != StateReadStatus.Success
            || baselineModel is not TModel before
            || !RuntimeModel<TModel, TFragment>.Diff(before, proposed.Result.Value!).IsEmpty
        )
        {
            throw RuntimeState.NewConflict(
                _diagnostics,
                "The migrated targets cannot replace the selected sources without changing the effective configuration."
            );
        }

        _validation.Validate(proposed.Result.Value!);
        foreach (var (target, _, desired) in targetPlans)
        {
            var outcome = targetResults.First(result => result.TargetId == target.Id);
            var latest = (
                await _engine.ReadSourceAsync(target, cancellationToken).ConfigureAwait(false)
            ).FromSource(target.Id, target.PhysicalOrigin);
            if (!string.Equals(latest.Revision, outcome.TargetRevision, StringComparison.Ordinal))
            {
                throw RuntimeState.NewConflict(
                    _diagnostics,
                    $"Target source '{target.Id}' changed while source retirement was being verified."
                );
            }

            if (latest.Status == StateReadStatus.NotFound && desired.IsEmpty)
            {
                continue;
            }

            if (latest.Status != StateReadStatus.Success || latest.Value is null)
            {
                throw RuntimeState.NewConflict(
                    _diagnostics,
                    $"Target source '{target.Id}' is not available for source retirement."
                );
            }

            var latestFragment = latest.Schema is { } latestSchema
                ? await _engine
                    .MigrateFragmentAsync(latest.Value, latestSchema, cancellationToken)
                    .ConfigureAwait(false)
                : latest.Value;
            if (
                (latest.Schema is { } actualSchema && actualSchema != currentSchema)
                || !ConfiglueFragmentComparer.AreEqual(latestFragment, desired)
            )
            {
                throw RuntimeState.NewConflict(
                    _diagnostics,
                    $"Target source '{target.Id}' no longer contains the verified migration result."
                );
            }
        }
    }

    private static void ValidateSourceKey(SourceKey<TModel> sourceKey, string parameterName)
    {
        if (sourceKey.IsDefault)
        {
            throw new ArgumentException("The source key is uninitialized.", parameterName);
        }
    }
}
