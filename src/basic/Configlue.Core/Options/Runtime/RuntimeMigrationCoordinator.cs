using Configlue.CompilerServices;
using Configlue.Sources;

namespace Configlue;

/// <summary>
/// Owns schema and storage migration for one runtime.
///
/// Orchestration only: fragment migration runs through the resolution engine's
/// diagnostics-wrapped path, while storage migration is decomposed into explicit
/// phases with clear ownership:
/// <list type="bullet">
/// <item>source selection and baseline capture (<see cref="RuntimeMigrationSourceStage{TModel,TFragment}"/>);</item>
/// <item>target projection and persistence (<see cref="RuntimeMigrationTargetStage{TModel,TFragment}"/>);</item>
/// <item>source revision/conflict validation before destructive actions (<see cref="RuntimeMigrationSnapshotGuard{TModel,TFragment}"/>);</item>
/// <item>source retirement and post-retirement verification (<see cref="RuntimeMigrationRetirementStage{TModel,TFragment}"/>).</item>
/// </list>
/// The supported contract is deliberately single-run and explicit: read old
/// representation/source, run schema migration, conditionally write the canonical
/// target, and optionally retire sources within this state instance under
/// application-controlled deployment logic. Core provides no durable progress
/// journal, no partial-target resume across restarts, and no distributed lease:
/// callers coordinate concurrent runs externally and retry by re-running the same
/// idempotent operation.
/// Planning (pure projection of merged fragments onto targets) never performs I/O;
/// every write funnels through one verified conditional-write pipeline, and
/// retirement runs only after the effective-model invariant is re-proved.
/// Narrow resolution snapshots are passed explicitly instead of sharing mutable
/// resolve state.
/// </summary>
internal sealed class RuntimeMigrationCoordinator<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly RuntimeResolutionEngine<TModel, TFragment> _engine;
    private readonly RuntimeDiagnosticRecorder _diagnostics;
    private readonly RuntimeLifetime _lifetime;
    private readonly RuntimeMigrationSourceStage<TModel, TFragment> _sources;
    private readonly RuntimeMigrationTargetStage<TModel, TFragment> _targets;
    private readonly RuntimeMigrationSnapshotGuard<TModel, TFragment> _guard;
    private readonly RuntimeMigrationRetirementStage<TModel, TFragment> _retirement;

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
        _diagnostics = diagnostics;
        _lifetime = lifetime;
        _sources = new RuntimeMigrationSourceStage<TModel, TFragment>(
            engine,
            topology,
            migrationSources
        );
        _targets = new RuntimeMigrationTargetStage<TModel, TFragment>(
            engine,
            writes,
            diagnostics,
            _sources
        );
        _guard = new RuntimeMigrationSnapshotGuard<TModel, TFragment>(engine, diagnostics);
        _retirement = new RuntimeMigrationRetirementStage<TModel, TFragment>(
            engine,
            topology,
            validation,
            diagnostics
        );
    }

    /// <summary>Migration-only sources, invisible to normal resolution.</summary>
    internal IReadOnlyList<StateSource<TFragment>> MigrationSources => _sources.MigrationSources;

    /// <summary>
    /// Resolves a migration participant from the active topology first and from the
    /// migration-only definitions second.
    /// </summary>
    internal StateSource<TFragment> FindMigrationSource(SourceId sourceId) =>
        _sources.FindMigrationSource(sourceId);

    internal bool IsMigrationOnlySource(SourceId sourceId) =>
        _sources.IsMigrationOnlySource(sourceId);

    internal async ValueTask<StateSourceMigrationResult> MigrateSourceAsync(
        SourceId sourceId,
        SourceId targetId,
        CancellationToken cancellationToken = default
    )
    {
        var diagnostic = _diagnostics.Start(ConfiglueDiagnosticOperation.Migrate, sourceId);
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
        cancellationToken.ThrowIfCancellationRequested();

        var (source, target) = _sources.ResolveSinglePair(sourceId, targetId);
        _sources.EnsureTargetWritable(target);

        var contribution = await _sources
            .ReadContributionAsync(source, allowNotFound: false, cancellationToken)
            .ConfigureAwait(false);

        if (IsIdentityMigration(source, target, contribution.Result))
        {
            return new StateSourceMigrationResult(
                source.Id,
                target.Id,
                contribution.Result.Revision,
                contribution.Result.Revision
            );
        }

        var baseRevision = await ResolveSingleWriteBaseAsync(
                source,
                target,
                contribution,
                cancellationToken
            )
            .ConfigureAwait(false);
        var write = await _targets
            .WriteVerifiedAsync(target, contribution.Fragment, baseRevision, cancellationToken)
            .ConfigureAwait(false);
        return new StateSourceMigrationResult(
            source.Id,
            target.Id,
            contribution.Result.Revision,
            write.Revision
        );
    }

    private static bool IsIdentityMigration(
        StateSource<TFragment> source,
        StateSource<TFragment> target,
        StateReadResult<TFragment> sourceResult
    )
    {
        // Same instance already at the current schema: nothing to copy.
        var currentSchema = RuntimeModel<TModel, TFragment>.Schema.ToMetadata();
        return ReferenceEquals(source, target)
            && (sourceResult.Schema is null || sourceResult.Schema == currentSchema);
    }

    /// <summary>
    /// Single shared write-base pipeline: same-instance and same-physical-resource
    /// migrations reuse the source revision captured through the old codec; only
    /// cross-resource migrations pre-read the target through its own codec.
    /// </summary>
    private async ValueTask<string?> ResolveSingleWriteBaseAsync(
        StateSource<TFragment> source,
        StateSource<TFragment> target,
        MigrationSourceContribution<TModel, TFragment> contribution,
        CancellationToken cancellationToken
    )
    {
        if (ReferenceEquals(source, target) || _sources.SharesPhysicalResource(source, target))
        {
            return contribution.Result.Revision;
        }

        var targetResult = await _engine
            .ReadMigrationSourceAsync(target, cancellationToken)
            .ConfigureAwait(false);
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

        return targetResult.Revision;
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
            ConfiglueDiagnosticOperation.Migrate,
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
        var canonical = _sources.FindMigrationSource(canonicalTargetId);
        _sources.EnsureTargetWritable(canonical);

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
            var adopted = await TryAdoptOneLegacyAsync(canonical, legacyId, cancellationToken)
                .ConfigureAwait(false);
            if (adopted is not null)
            {
                return adopted;
            }
        }

        return null;
    }

    /// <summary>
    /// Probes one legacy representation; returns null when it holds no migratable
    /// state so the caller continues with the next representation.
    /// </summary>
    private async ValueTask<StateSourceMigrationResult?> TryAdoptOneLegacyAsync(
        StateSource<TFragment> canonical,
        SourceId legacyId,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var legacy = _sources.FindMigrationSource(legacyId);
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
            return null;
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

        string? baseRevision;
        if (
            !ReferenceEquals(legacy, canonical)
            && _sources.SharesPhysicalResource(legacy, canonical)
        )
        {
            baseRevision = legacyResult.Revision;
        }
        else
        {
            // Re-read the canonical target so a concurrent canonical write wins over legacy input.
            var canonicalBase = await ReadCanonicalBaseForAdoptionAsync(
                    canonical,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (!canonicalBase.ShouldAdopt)
            {
                return null;
            }

            baseRevision = canonicalBase.BaseRevision;
        }

        var write = await _targets
            .WriteVerifiedAsync(canonical, fragment, baseRevision, cancellationToken)
            .ConfigureAwait(false);
        return new StateSourceMigrationResult(
            legacy.Id,
            canonical.Id,
            legacyResult.Revision,
            write.Revision
        );
    }

    /// <summary>
    /// Re-reads the canonical target before adopting legacy input. Yields
    /// <c>ShouldAdopt: false</c> when canonical state appeared concurrently
    /// (adoption must not overwrite it); otherwise yields the write base revision,
    /// which is itself null when the canonical target is still absent.
    /// </summary>
    private async ValueTask<(
        bool ShouldAdopt,
        string? BaseRevision
    )> ReadCanonicalBaseForAdoptionAsync(
        StateSource<TFragment> canonical,
        CancellationToken cancellationToken
    )
    {
        var current = await _engine
            .ReadMigrationSourceAsync(canonical, cancellationToken)
            .ConfigureAwait(false);
        if (current.Status == StateReadStatus.Unavailable)
        {
            throw new InvalidOperationException($"Target source '{canonical.Id}' is unavailable.");
        }

        if (current.Status == StateReadStatus.Success)
        {
            return (ShouldAdopt: false, BaseRevision: null);
        }

        return (ShouldAdopt: true, BaseRevision: current.Revision);
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
        var diagnostic = _diagnostics.Start(ConfiglueDiagnosticOperation.Migrate);
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
        var requestedSourceIds = ValidateBulkSelection(sourceIds, targetProjections);

        object? resolvedBeforeMigration = retireSources
            ? await _retirement
                .CaptureResolvedBaselineAsync(cancellationToken)
                .ConfigureAwait(false)
            : null;

        var orderedSources = _sources.SelectOrderedSources(
            requestedSourceIds,
            targetProjections,
            retireSources
        );
        var baseline = await _sources
            .ReadBaselineAsync(orderedSources, cancellationToken)
            .ConfigureAwait(false);

        var merged = RuntimeMigrationSourceStage<TModel, TFragment>.MergeContributions(
            baseline.Contributions
        );
        var targetPlans = _targets.BuildTargetPlans(merged, targetProjections);

        var targetResults = await PersistTargetsAsync(baseline, targetPlans, cancellationToken)
            .ConfigureAwait(false);
        var retiredSourceIds = retireSources
            ? await RetireSourcesAsync(
                    resolvedBeforeMigration!,
                    baseline,
                    targetPlans,
                    targetResults,
                    cancellationToken
                )
                .ConfigureAwait(false)
            : [];

        return new StateStorageMigrationResult(
            baseline.Contributions.Select(static contribution => contribution.Source.Id),
            new StateRevisionVector(baseline.Revisions),
            targetResults,
            retiredSourceIds
        );
    }

    private static SourceId[] ValidateBulkSelection(
        IEnumerable<SourceId> sourceIds,
        IReadOnlyDictionary<SourceId, Func<TFragment, TFragment>> targetProjections
    )
    {
        ArgumentNullException.ThrowIfNull(sourceIds);
        ArgumentNullException.ThrowIfNull(targetProjections);
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

        return requestedSourceIds;
    }

    /// <summary>Persists every planned target with snapshot validation around each write.</summary>
    private async ValueTask<List<StateStorageMigrationTargetResult>> PersistTargetsAsync(
        MigrationBaseline<TModel, TFragment> baseline,
        List<MigrationTargetPlan<TModel, TFragment>> targetPlans,
        CancellationToken cancellationToken
    )
    {
        var targetResults = new List<StateStorageMigrationTargetResult>(targetPlans.Count);
        foreach (var plan in targetPlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _guard.VerifyUnchangedAsync(baseline, cancellationToken).ConfigureAwait(false);
            var result = await MigrateOneTargetAsync(baseline, plan, cancellationToken)
                .ConfigureAwait(false);
            targetResults.Add(result);
        }

        return targetResults;
    }

    private async ValueTask<StateStorageMigrationTargetResult> MigrateOneTargetAsync(
        MigrationBaseline<TModel, TFragment> baseline,
        MigrationTargetPlan<TModel, TFragment> plan,
        CancellationToken cancellationToken
    )
    {
        var currentModelSchema = RuntimeModel<TModel, TFragment>.Schema.ToMetadata();
        var current = await _targets
            .ReadCurrentAsync(plan.Target, baseline.Contributions, cancellationToken)
            .ConfigureAwait(false);

        if (
            RuntimeMigrationTargetStage<TModel, TFragment>.IsAlreadyCurrent(
                current,
                plan.Desired,
                current.Result.Schema,
                currentModelSchema
            )
        )
        {
            await _targets
                .ConfirmAlreadyCurrentAsync(
                    plan.Target,
                    current,
                    plan.Desired,
                    currentModelSchema,
                    cancellationToken
                )
                .ConfigureAwait(false);
            await _guard.VerifyUnchangedAsync(baseline, cancellationToken).ConfigureAwait(false);
            return new StateStorageMigrationTargetResult(
                plan.Target.Id,
                current.Result.Revision,
                current.Result.Revision,
                WasAlreadyCurrent: true
            );
        }

        await _guard.VerifyUnchangedAsync(baseline, cancellationToken).ConfigureAwait(false);
        var write = await _targets
            .WriteVerifiedAsync(
                plan.Target,
                plan.Desired,
                current.WriteBaseRevision,
                cancellationToken
            )
            .ConfigureAwait(false);
        return new StateStorageMigrationTargetResult(
            plan.Target.Id,
            current.Result.Revision,
            write.Revision,
            WasAlreadyCurrent: false
        );
    }

    /// <summary>Re-validates snapshots and targets, then retires sources.</summary>
    private async ValueTask<SourceId[]> RetireSourcesAsync(
        object resolvedBeforeMigration,
        MigrationBaseline<TModel, TFragment> baseline,
        IReadOnlyList<MigrationTargetPlan<TModel, TFragment>> targetPlans,
        IReadOnlyList<StateStorageMigrationTargetResult> targetResults,
        CancellationToken cancellationToken
    )
    {
        await _guard.VerifyUnchangedAsync(baseline, cancellationToken).ConfigureAwait(false);
        var retired = await _retirement
            .VerifyAndRetireAsync(
                resolvedBeforeMigration,
                baseline,
                targetPlans,
                targetResults,
                cancellationToken
            )
            .ConfigureAwait(false);
        await _guard.VerifyUnchangedAsync(baseline, cancellationToken).ConfigureAwait(false);
        return retired;
    }

    private static void ValidateSourceKey(SourceKey<TModel> sourceKey, string parameterName)
    {
        if (sourceKey.IsDefault)
        {
            throw new ArgumentException("The source key is uninitialized.", parameterName);
        }
    }
}
