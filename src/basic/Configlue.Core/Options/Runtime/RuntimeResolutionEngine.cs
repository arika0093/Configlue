using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// Owns state resolution for one runtime: source reads, layered merge, schema
/// migration of source fragments, and resolved-model validation.
///
/// Holds the model-defaults contribution, the schema-migration chain, and the
/// read-validation mode. Source topology, subject scoping, diagnostics, lifetime,
/// and validation are referenced collaborators owned elsewhere; the active-source
/// snapshot is always re-read from the topology so retirement is observed.
/// </summary>
internal sealed partial class RuntimeResolutionEngine<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly RuntimeSourceTopology<TFragment> _topology;
    private readonly RuntimeSubjectContext _subjects;
    private readonly RuntimeDiagnosticRecorder _diagnostics;
    private readonly RuntimeLifetime _lifetime;
    private readonly RuntimeValidationPipeline<TModel, TFragment> _validation;
    private readonly RuntimeModelCloner<TModel, TFragment> _cloner;
    private readonly StateSchemaMigrationChain<TFragment> _migrationChain;
    private readonly int _migrationCount;
    private readonly TFragment _modelDefaultsFragment;
    private readonly StateSource<TFragment> _modelDefaultsSource;
    private readonly ReadValidationMode _readValidationMode;

    internal RuntimeResolutionEngine(
        RuntimeSourceTopology<TFragment> topology,
        RuntimeSubjectContext subjects,
        RuntimeDiagnosticRecorder diagnostics,
        RuntimeLifetime lifetime,
        RuntimeValidationPipeline<TModel, TFragment> validation,
        RuntimeModelCloner<TModel, TFragment> cloner,
        IEnumerable<IStateSchemaMigration<TFragment>>? migrations,
        ReadValidationMode readValidationMode
    )
    {
        _topology = topology;
        _subjects = subjects;
        _diagnostics = diagnostics;
        _lifetime = lifetime;
        _validation = validation;
        _cloner = cloner;
        var migrationList = migrations?.ToArray() ?? [];
        _migrationCount = migrationList.Length;
        _migrationChain = new StateSchemaMigrationChain<TFragment>(
            RuntimeModel<TModel, TFragment>.Schema.ToMetadata(),
            migrationList
        );
        _modelDefaultsFragment = RuntimeModel<TModel, TFragment>.ToFragment(
            RuntimeModel<TModel, TFragment>.FromFragment(
                RuntimeModel<TModel, TFragment>.EmptyFragment
            )
        );
        _modelDefaultsSource = new StateSource<TFragment>(
            $"__configlue_model_defaults:{Guid.NewGuid():N}",
            new RuntimeModelDefaultsReader<TFragment>(_modelDefaultsFragment),
            new StateSourceOptions<TFragment>()
        );
        _readValidationMode = readValidationMode;
    }

    internal int MigrationCount => _migrationCount;

    internal TFragment ModelDefaultsFragment => _modelDefaultsFragment;

    internal SourceId ModelDefaultsSourceId => _modelDefaultsSource.Id;

    internal ReadValidationMode ReadValidationMode => _readValidationMode;

    internal ValueTask<StateReadResult<TFragment>> ReadSourceAsync(
        StateSource<TFragment> source,
        CancellationToken cancellationToken
    ) => ReadSourceAsync(source, GetResourceContext(source), cancellationToken);

    internal ValueTask<StateReadResult<TFragment>> ReadSourceAsync(
        StateSource<TFragment> source,
        ConfiglueResourceContext? context,
        CancellationToken cancellationToken,
        long parentOperationId = 0
    )
    {
        var diagnostic = _diagnostics.Start(
            ConfiglueDiagnosticEventKind.SourceReadStarted,
            source.Id,
            parentOperationId
        );
        return diagnostic.Id == 0
            ? source.ReadAsync(
                context ?? RuntimeModel<TModel, TFragment>.DefaultResourceContext,
                cancellationToken
            )
            : ReadObservedSourceAsync(source, context, cancellationToken, diagnostic);
    }

    private static async ValueTask<StateReadResult<TFragment>> ReadObservedSourceAsync(
        StateSource<TFragment> source,
        ConfiglueResourceContext? context,
        CancellationToken cancellationToken,
        RuntimeDiagnosticRecorder.DiagnosticOperation diagnostic
    )
    {
        try
        {
            var result = await source
                .ReadAsync(
                    context ?? RuntimeModel<TModel, TFragment>.DefaultResourceContext,
                    cancellationToken
                )
                .ConfigureAwait(false);
            diagnostic.Complete(
                ConfiglueDiagnosticEventKind.SourceReadCompleted,
                result.Status,
                result.Revision is not null
            );
            return result;
        }
        catch (Exception exception)
        {
            diagnostic.Fail(
                ConfiglueDiagnosticEventKind.SourceReadFailed,
                exception,
                exception is OperationCanceledException && cancellationToken.IsCancellationRequested
            );
            throw;
        }
    }

    internal async ValueTask<StateReadResult<TFragment>> ReadMigrationSourceAsync(
        StateSource<TFragment> source,
        CancellationToken cancellationToken
    )
    {
        var result = await ReadSourceAsync(source, cancellationToken).ConfigureAwait(false);
        return result.FromSource(source.Id, source.PhysicalOrigin);
    }

    internal ConfiglueResourceContext GetResourceContext(StateSource<TFragment> source) =>
        _subjects.GetResourceContext(
            source,
            RuntimeModel<TModel, TFragment>.DefaultResourceContext
        );

    internal ResourceId? GetResourceId(StateSource<TFragment> source) =>
        _subjects.GetResourceId(source, RuntimeModel<TModel, TFragment>.DefaultResourceContext);

    internal async ValueTask<TFragment> MigrateFragmentAsync(
        TFragment value,
        StateSchemaMetadata sourceSchema,
        CancellationToken cancellationToken,
        SourceId? sourceId = null,
        long parentOperationId = 0
    )
    {
        var diagnostic = _diagnostics.Start(
            ConfiglueDiagnosticEventKind.MigrationStarted,
            sourceId,
            parentOperationId
        );
        try
        {
            var result = await _migrationChain
                .MigrateAsync(value, sourceSchema, cancellationToken)
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

    internal async ValueTask<StateReadResult<TModel>> ReadPublicValueAsync(
        CancellationToken cancellationToken,
        long parentOperationId = 0
    )
    {
        var result = await ReadModelAsync(null, cancellationToken, parentOperationId)
            .ConfigureAwait(false);
        return result.Status == StateReadStatus.Success && result.Value is not null
            ? result.WithValue(_cloner.Clone(result.Value))
            : result;
    }

    // Raw resolved read for internal callers and tests. The public read-side capability surface exposes
    // GetValueAsync, generated GetDetailsAsync, and Check instead.
    internal async ValueTask<StateReadResult<TModel>> ReadModelAsync(
        IReadOnlyDictionary<SourceId, StateReadResult<TFragment>>? replacements,
        CancellationToken cancellationToken,
        long parentOperationId = 0
    ) =>
        (
            await ResolveAsync(
                    replacements,
                    cancellationToken,
                    parentOperationId: parentOperationId
                )
                .ConfigureAwait(false)
        ).Result;

    internal ValueTask<ResolvedState<TModel, TFragment>> ResolveAsync(
        IReadOnlyDictionary<SourceId, StateReadResult<TFragment>>? replacements,
        CancellationToken cancellationToken,
        bool captureContributions = false,
        Action<ResolvedSourceProbe<TFragment>>? observeSource = null,
        long parentOperationId = 0
    )
    {
        if (replacements is not null)
        {
            return ResolveImplementationAsync(
                replacements,
                cancellationToken,
                captureContributions,
                observeSource
            );
        }
        var diagnostic = _diagnostics.Start(
            ConfiglueDiagnosticEventKind.ResolveStarted,
            parentOperationId: parentOperationId
        );
        return diagnostic.Id == 0
            ? ResolveImplementationAsync(
                null,
                cancellationToken,
                captureContributions,
                observeSource
            )
            : ResolveObservedAsync(
                cancellationToken,
                captureContributions,
                observeSource,
                diagnostic
            );
    }

    private async ValueTask<ResolvedState<TModel, TFragment>> ResolveObservedAsync(
        CancellationToken cancellationToken,
        bool captureContributions,
        Action<ResolvedSourceProbe<TFragment>>? observeSource,
        RuntimeDiagnosticRecorder.DiagnosticOperation diagnostic
    )
    {
        try
        {
            var result = await ResolveImplementationAsync(
                    null,
                    cancellationToken,
                    captureContributions,
                    observeSource,
                    diagnostic.Id
                )
                .ConfigureAwait(false);
            diagnostic.Complete(
                ConfiglueDiagnosticEventKind.ResolveCompleted,
                result.Result.Status,
                result.Result.Revision is not null
            );
            return result;
        }
        catch (Exception exception)
        {
            if (exception is ConfiglueValidationException)
            {
                _diagnostics.Record(
                    ConfiglueDiagnosticEventKind.ValidationFailed,
                    diagnostic.Id,
                    errorCategory: exception.GetType().FullName
                );
            }
            diagnostic.Fail(
                ConfiglueDiagnosticEventKind.ResolveFailed,
                exception,
                exception is OperationCanceledException && cancellationToken.IsCancellationRequested
            );
            throw;
        }
    }

    private async ValueTask<ResolvedState<TModel, TFragment>> ResolveImplementationAsync(
        IReadOnlyDictionary<SourceId, StateReadResult<TFragment>>? replacements,
        CancellationToken cancellationToken,
        bool captureContributions = false,
        Action<ResolvedSourceProbe<TFragment>>? observeSource = null,
        long operationId = 0
    )
    {
        using var operation = _lifetime.EnterOperation();
        var activeSources = _topology.GetActiveSources();
        var subject = _subjects.Current;
        List<ResolvedContribution<TFragment>>? contributions = captureContributions
            ? new List<ResolvedContribution<TFragment>>(activeSources.Length + 1)
            : null;
        // Scratch fragments and revision observations are freshly allocated per
        // resolution and never retained (issue #276). Pooling them saved on the order
        // of a hundred bytes per multi-source resolution while adding
        // Rent/Clear/Return discipline to the hottest runtime path; the single-source
        // fast path below (the simple-settings topology) already avoids arrays
        // entirely via locals.
        TFragment[]? fragments =
            !captureContributions && activeSources.Length > 1
                ? new TFragment[activeSources.Length + 1]
                : null;
        TFragment singleFragment = default!;
        List<ResolvedFailure<TFragment>>? failures = null;
        StateRevision[]? revisions =
            activeSources.Length > 1 ? new StateRevision[activeSources.Length] : null;
        var singleRevision = default(StateRevision);
        var revisionCount = 0;
        KeyValuePair<SourceId, StateRevisionVector>[]? nestedRevisions = null;
        var nestedRevisionCount = 0;
        StateReadResult<TFragment> lastFailure = default;
        StateSource<TFragment>? activeSource = null;
        StateReadResult<TFragment> activeResult = default;
        var successfulCount = 0;

        foreach (var source in activeSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConfiglueResourceContext? resourceContext = subject is null
                ? null
                : source.GetResourceContext(subject);
            ResourceId? resourceId = null;
            if (captureContributions || observeSource is not null)
            {
                resourceId = resourceContext is not null
                    ? source.GetResourceId(resourceContext.Value)
                    : source.GetResourceId(RuntimeModel<TModel, TFragment>.DefaultResourceContext);
            }
            StateReadResult<TFragment> sourceResult;
            if (
                replacements is not null
                && replacements.TryGetValue(source.Id, out var replacement)
            )
            {
                sourceResult = replacement;
            }
            else
            {
                try
                {
                    sourceResult = await ReadSourceAsync(
                            source,
                            resourceContext,
                            cancellationToken,
                            operationId
                        )
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                // Preserve the codec's original exception type so its recoverability policy can classify it.
                catch (Exception exception)
                {
                    observeSource?.Invoke(
                        new ResolvedSourceProbe<TFragment>
                        {
                            Source = source,
                            Contributed = false,
                            FallbackContinued = false,
                            ResourceContext = resourceContext,
                            ResourceId = resourceId,
                            Exception = exception,
                        }
                    );
                    throw;
                }
            }

            var result = sourceResult.FromSource(source.Id, source.PhysicalOrigin);
            var sourceRevision = new StateRevision(source.Id, result.Revision);
            if (revisions is null)
            {
                singleRevision = sourceRevision;
            }
            else
            {
                revisions[revisionCount] = sourceRevision;
            }
            revisionCount++;
            if (sourceResult.Revisions is { } nestedVector)
            {
                nestedRevisions ??= new KeyValuePair<SourceId, StateRevisionVector>[
                    activeSources.Length
                ];
                nestedRevisions[nestedRevisionCount++] = new(source.Id, nestedVector);
            }

            if (result.Status == StateReadStatus.Success)
            {
                if (result.Value is null)
                {
                    throw new InvalidOperationException(
                        $"State source '{source.Id}' returned a null configuration fragment."
                    );
                }

                var fragment = result.Value;
                if (result.Schema is { } sourceSchema)
                {
                    fragment = await MigrateFragmentAsync(
                            fragment,
                            sourceSchema,
                            cancellationToken,
                            source.Id,
                            operationId
                        )
                        .ConfigureAwait(false);
                }

                // Read validation governs source state; proposal resolutions (replacements)
                // are validated by their write paths instead.
                if (replacements is null && _readValidationMode == ReadValidationMode.StrictThrow)
                {
                    _validation.ValidateContribution(source, fragment, _modelDefaultsFragment);
                }
                else if (
                    replacements is null
                    && _readValidationMode == ReadValidationMode.IgnoreValue
                )
                {
                    var pruned = _validation.PruneInvalidMembers(
                        source,
                        fragment,
                        _modelDefaultsFragment
                    );
                    if (pruned is TFragment prunedFragment)
                    {
                        fragment = prunedFragment;
                    }
                }

                if (captureContributions)
                {
                    contributions!.Add(
                        new ResolvedContribution<TFragment>(
                            source,
                            result.WithValue(fragment),
                            ResourceContext: resourceContext,
                            ResourceId: resourceId
                        )
                    );
                }
                else
                {
                    if (fragments is null)
                    {
                        singleFragment = fragment;
                    }
                    else
                    {
                        fragments[successfulCount] = fragment;
                    }
                }

                if (activeSource is null)
                {
                    activeSource = source;
                    activeResult = result.WithValue(fragment);
                }
                successfulCount++;
                observeSource?.Invoke(
                    new ResolvedSourceProbe<TFragment>
                    {
                        Source = source,
                        Result = result.WithValue(fragment),
                        Contributed = true,
                        FallbackContinued = false,
                        ResourceContext = resourceContext,
                        ResourceId = resourceId,
                    }
                );
                continue;
            }

            lastFailure = result;
            var canFallBack = RuntimeState.CanFallBack(source.FallbackCondition, result.Status);
            if (canFallBack && replacements is null)
            {
                _diagnostics.Record(
                    ConfiglueDiagnosticEventKind.SourceFallback,
                    operationId,
                    sourceId: source.Id,
                    readStatus: result.Status
                );
            }
            observeSource?.Invoke(
                new ResolvedSourceProbe<TFragment>
                {
                    Source = source,
                    Result = result,
                    Contributed = false,
                    FallbackContinued = canFallBack,
                    ResourceContext = resourceContext,
                    ResourceId = resourceId,
                }
            );
            if (!canFallBack)
            {
                if (captureContributions)
                {
                    (failures ??= []).Add(
                        new ResolvedFailure<TFragment>(
                            source,
                            result,
                            ResourceContext: resourceContext,
                            ResourceId: resourceId
                        )
                    );
                }

                return new ResolvedState<TModel, TFragment>(
                    StateReadResult<TModel>.Create(
                        result.Status,
                        default,
                        result.Revision,
                        result.SourceId,
                        result.PhysicalOrigin,
                        result.Schema,
                        CreateRevisionVector(
                            activeSources,
                            revisions,
                            singleRevision,
                            revisionCount,
                            nestedRevisions,
                            nestedRevisionCount
                        )
                    ),
                    (IReadOnlyList<ResolvedContribution<TFragment>>?)contributions
                        ?? Array.Empty<ResolvedContribution<TFragment>>(),
                    null,
                    (IReadOnlyList<ResolvedFailure<TFragment>>?)failures
                        ?? Array.Empty<ResolvedFailure<TFragment>>()
                );
            }

            if (captureContributions)
            {
                (failures ??= []).Add(
                    new ResolvedFailure<TFragment>(
                        source,
                        result,
                        ResourceContext: resourceContext,
                        ResourceId: resourceId
                    )
                );
            }
        }

        if (successfulCount == 0 && lastFailure.Status == StateReadStatus.Unavailable)
        {
            return new ResolvedState<TModel, TFragment>(
                StateReadResult<TModel>.Create(
                    lastFailure.Status,
                    default,
                    lastFailure.Revision,
                    lastFailure.SourceId,
                    lastFailure.PhysicalOrigin,
                    lastFailure.Schema,
                    CreateRevisionVector(
                        activeSources,
                        revisions,
                        singleRevision,
                        revisionCount,
                        nestedRevisions,
                        nestedRevisionCount
                    )
                ),
                (IReadOnlyList<ResolvedContribution<TFragment>>?)contributions
                    ?? Array.Empty<ResolvedContribution<TFragment>>(),
                null,
                (IReadOnlyList<ResolvedFailure<TFragment>>?)failures
                    ?? Array.Empty<ResolvedFailure<TFragment>>()
            );
        }

        if (captureContributions)
        {
            contributions!.Add(
                new ResolvedContribution<TFragment>(
                    _modelDefaultsSource,
                    StateReadResult<TFragment>.Success(_modelDefaultsFragment),
                    IsModelDefaults: true
                )
            );
        }
        else
        {
            if (fragments is not null)
            {
                fragments[successfulCount] = _modelDefaultsFragment;
            }
        }

        var contributionCount = successfulCount + 1;
        TFragment merged;
        if (captureContributions)
        {
            merged = contributions![^1].Result.Value!;
        }
        else if (fragments is null)
        {
            merged =
                successfulCount == 0
                    ? _modelDefaultsFragment
                    : _modelDefaultsFragment.Merge(singleFragment);
        }
        else
        {
            merged = fragments[successfulCount];
        }

        var mergeStartIndex =
            fragments is null && !captureContributions ? -1 : contributionCount - 2;
        for (var index = mergeStartIndex; index >= 0; index--)
        {
            var fragment = captureContributions
                ? contributions![index].Result.Value!
                : fragments![index];
            merged = merged.Merge(fragment);
        }

        var model = RuntimeModel<TModel, TFragment>.FromFragment(merged);
        if (replacements is null)
        {
            _validation.ValidateResolvedModel(model, merged);
        }
        var resolvedResult = StateReadResult<TModel>.Success(
            model,
            activeSource is null ? null : activeResult.Revision,
            RuntimeModel<TModel, TFragment>.Schema.ToMetadata()
        ) with
        {
            SourceId = activeSource?.Id,
            PhysicalOrigin = activeSource is null ? null : activeResult.PhysicalOrigin,
            Revisions = CreateRevisionVector(
                activeSources,
                revisions,
                singleRevision,
                revisionCount,
                nestedRevisions,
                nestedRevisionCount
            ),
        };

        return new ResolvedState<TModel, TFragment>(
            resolvedResult,
            (IReadOnlyList<ResolvedContribution<TFragment>>?)contributions
                ?? Array.Empty<ResolvedContribution<TFragment>>(),
            merged,
            (IReadOnlyList<ResolvedFailure<TFragment>>?)failures
                ?? Array.Empty<ResolvedFailure<TFragment>>()
        );
    }
}
