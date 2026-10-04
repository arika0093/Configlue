using Configlue.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Configlue;

/// <summary>
/// Facade over the internal runtime coordinators for one generated model.
///
/// All read/write/watch/migration/subject state lives in the directly-owned
/// components below; this type only constructs them, exposes the public
/// capability surface, and coordinates shutdown. See
/// <c>docs/internal/runtime-coordinators.md</c> for where new behavior belongs.
/// </summary>
internal sealed partial class ConfiglueRuntime<TModel, TFragment>
    : IConfiglueRuntimeState<TModel>,
        IConfiglueValueCloneProvider<TModel>,
        IConfiglueReloadFailureDiagnostics<TModel>,
        IConfiglueRuntimeLifetimeProvider,
        ISubjectState<TModel>,
        IConfiglueWritePreview<TModel>,
        IDisposable,
        IAsyncDisposable
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly string _stateName;
    private readonly RuntimeLifetime _lifetime;
    private readonly RuntimeSubjectContext _subjects;
    private readonly RuntimeSourceTopology<TFragment> _topology;
    private readonly RuntimeDiagnosticRecorder _diagnostics;
    private readonly RuntimeValidationPipeline<TModel, TFragment> _validation;
    private readonly RuntimeModelCloner<TModel, TFragment> _cloner;
    private readonly RuntimeResolutionEngine<TModel, TFragment> _resolution;
    private readonly RuntimeWriteCoordinator<TModel, TFragment> _writes;
    private readonly RuntimeMigrationCoordinator<TModel, TFragment> _migrations;
    private readonly RuntimeWatchCoordinator<TModel, TFragment> _watches;
    private readonly RuntimeInspectionCoordinator<TModel, TFragment> _inspection;
    private readonly RuntimeEditSessionCoordinator<TModel, TFragment> _edits;

    RuntimeLifetimeRequirement IConfiglueRuntimeLifetimeProvider.RuntimeLifetime =>
        _topology.SourceSet.RuntimeLifetime;

    /// <summary>
    /// Internal synchronization hook invoked immediately before a subject watcher removes itself
    /// from tracking and disposes its cancellation source. Tests use it to block the final
    /// lifetime cleanup while asserting that shutdown drains the owned completion.
    /// </summary>
    internal Func<Task>? WatcherCleanupBarrier
    {
        get => _watches.WatcherCleanupBarrier;
        set => _watches.WatcherCleanupBarrier = value;
    }

    /// <summary>Number of subject watchers whose complete lifetime has not yet been drained.</summary>
    internal int WatcherOperationCount => _watches.WatcherOperationCount;

    // Contract-test barrier between the post-write snapshot read and its result publication.
    internal Func<CancellationToken, ValueTask>? AfterEditSessionSnapshotResolved
    {
        get => _edits.AfterEditSessionSnapshotResolved;
        set => _edits.AfterEditSessionSnapshotResolved = value;
    }

    /// <summary>Creates state backed by the supplied sources.</summary>
    public ConfiglueRuntime(
        StateSourceSet<TFragment> sourceSet,
        StateWritePlan? defaultWritePlan = null,
        IEnumerable<IStateSchemaMigration<TFragment>>? migrations = null,
        IEnumerable<IConfiglueValidator<TModel>>? validators = null,
        bool validateDataAnnotations = true,
        TimeSpan? onChangeDebounce = null,
        string? stateName = null,
        ILogger? logger = null,
        ReadValidationMode readValidationMode = ReadValidationMode.EffectiveThrow,
        WriteConflictResolution writeConflictResolution = WriteConflictResolution.FailOnConflict,
        ConfiglueRuntimeDiagnosticOptions? diagnostics = null,
        TimeProvider? timeProvider = null,
        StateSourceSet<TFragment>? migrationSources = null
    )
        : this(
            sourceSet,
            defaultWritePlan,
            migrations,
            validators,
            validateDataAnnotations,
            onChangeDebounce,
            stateName,
            logger,
            cloneStrategy: null,
            readValidationMode: readValidationMode,
            writeConflictResolution: writeConflictResolution,
            diagnostics: diagnostics,
            timeProvider: timeProvider,
            migrationSources: migrationSources
        ) { }

    /// <summary>Creates state with a custom model clone strategy.</summary>
    public ConfiglueRuntime(
        StateSourceSet<TFragment> sourceSet,
        StateWritePlan? defaultWritePlan,
        IEnumerable<IStateSchemaMigration<TFragment>>? migrations,
        IEnumerable<IConfiglueValidator<TModel>>? validators,
        bool validateDataAnnotations,
        TimeSpan? onChangeDebounce,
        string? stateName,
        ILogger? logger,
        Func<TModel, TModel>? cloneStrategy,
        ReadValidationMode readValidationMode = ReadValidationMode.EffectiveThrow,
        WriteConflictResolution writeConflictResolution = WriteConflictResolution.FailOnConflict,
        ConfiglueRuntimeDiagnosticOptions? diagnostics = null,
        TimeProvider? timeProvider = null,
        StateSourceSet<TFragment>? migrationSources = null
    )
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        _stateName = stateName ?? string.Empty;
        if (!Enum.IsDefined(readValidationMode))
        {
            throw new ArgumentOutOfRangeException(nameof(readValidationMode));
        }

        if (!Enum.IsDefined(writeConflictResolution))
        {
            throw new ArgumentOutOfRangeException(nameof(writeConflictResolution));
        }

        var onChangeDebounceValue = onChangeDebounce ?? TimeSpan.FromMilliseconds(300);
        if (onChangeDebounceValue < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(onChangeDebounce),
                "Change debounce cannot be negative."
            );
        }

        var validatorList = validators?.ToArray() ?? [];
        if (validatorList.Any(static validator => validator is null))
        {
            throw new ArgumentException(
                "Validators cannot contain null values.",
                nameof(validators)
            );
        }

        _lifetime = new RuntimeLifetime(this);
        _subjects = new RuntimeSubjectContext();
        _topology = new RuntimeSourceTopology<TFragment>(
            sourceSet,
            RuntimeModel<TModel, TFragment>.Schema.Id
        );
        _diagnostics = new RuntimeDiagnosticRecorder(
            _stateName,
            RuntimeModel<TModel, TFragment>.Schema.Id,
            RuntimeModel<TModel, TFragment>.Schema.Version,
            diagnostics ?? ConfiglueRuntimeDiagnosticOptions.Default,
            _topology
                .GetActiveSources()
                .Select(static source => new ConfiglueRuntimeSourceSnapshot(
                    source.Id,
                    source.Reader.GetType().FullName ?? source.Reader.GetType().Name,
                    true,
                    true,
                    source.Writer is not null,
                    source.Watcher is not null,
                    false,
                    null,
                    null,
                    null
                )),
            logger,
            subjectKeyProvider: () => _subjects.CurrentKey
        );
        _validation = new RuntimeValidationPipeline<TModel, TFragment>(
            validatorList,
            validateDataAnnotations,
            _stateName,
            _diagnostics
        );
        _cloner = new RuntimeModelCloner<TModel, TFragment>(cloneStrategy);
        _resolution = new RuntimeResolutionEngine<TModel, TFragment>(
            _topology,
            _subjects,
            _diagnostics,
            _lifetime,
            _validation,
            _cloner,
            migrations,
            readValidationMode
        );
        _writes = new RuntimeWriteCoordinator<TModel, TFragment>(
            _topology,
            _resolution,
            _validation,
            _diagnostics,
            _lifetime,
            _subjects,
            _cloner,
            _stateName,
            defaultWritePlan ?? StateWritePlan.Empty
        );
        _topology.TryEnableSingleSourceFastPath(_writes.Plan, _resolution.MigrationCount);
        _watches = new RuntimeWatchCoordinator<TModel, TFragment>(
            _resolution,
            _topology,
            _diagnostics,
            _lifetime,
            _subjects,
            _cloner,
            onChangeDebounceValue,
            timeProvider ?? TimeProvider.System
        );
        _inspection = new RuntimeInspectionCoordinator<TModel, TFragment>(
            _resolution,
            _topology,
            _writes,
            _subjects,
            _stateName
        );
        _edits = new RuntimeEditSessionCoordinator<TModel, TFragment>(
            _resolution,
            _writes,
            _inspection,
            _diagnostics,
            _lifetime,
            _subjects,
            _cloner,
            writeConflictResolution
        );
        _migrations = new RuntimeMigrationCoordinator<TModel, TFragment>(
            _resolution,
            _topology,
            _writes,
            _validation,
            _diagnostics,
            _lifetime,
            migrationSources: migrationSources?.Sources
        );
    }

    /// <inheritdoc />
    public ConfiglueRuntimeDiagnosticSnapshot GetRuntimeSnapshot() =>
        _diagnostics.GetRuntimeSnapshot();

    /// <inheritdoc />
    public IReadOnlyList<ConfiglueDiagnosticEvent> GetRecentEvents() =>
        _diagnostics.GetRecentEvents();

    /// <inheritdoc />
    public IDisposable OnDiagnosticEvent(Action<ConfiglueDiagnosticEvent> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return _lifetime.Register(() => _diagnostics.OnDiagnosticEvent(listener));
    }

    /// <inheritdoc />
    public ConfiglueStateDiagnostics GetDiagnostics() => _inspection.GetDiagnostics();

    /// <inheritdoc />
    public IDisposable OnChange(Action<TModel> listener) => _watches.OnChange(listener);

    /// <inheritdoc />
    public IDisposable OnReload(Action<StateRevisionVector?> listener) =>
        _watches.OnReload(listener);

    /// <inheritdoc />
    public IDisposable OnReloadFailed(Action<Exception> listener) =>
        _watches.OnReloadFailed(listener);

    /// <inheritdoc />
    public IWritableState<TModel> ForSubject(IConfiglueSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return new SubjectBoundOptions(this, subject);
    }

    /// <inheritdoc />
    public IConfiglueEditSessions<TModel> EditSessionsForSubject(IConfiglueSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return new SubjectBoundOptions(this, subject);
    }

    private async ValueTask<TModel> GetValueForSubjectAsync(
        IConfiglueSubject subject,
        CancellationToken cancellationToken
    )
    {
        using var scope = _subjects.Enter(subject);
        return await GetValueAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<StateWriteReceipt> SaveForSubjectAsync(
        IConfiglueSubject subject,
        IConfiglueModelPatch<TModel> patch,
        CancellationToken cancellationToken
    )
    {
        using var scope = _subjects.Enter(subject);
        return await SaveAsync(patch, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<EditSession<TModel>> OpenEditSessionForSubjectAsync(
        IConfiglueSubject subject,
        StateWritePlan? writePlan,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(subject);
        return _edits.OpenEditSessionCoreAsync(
            writePlan,
            cancellationToken,
            pinnedSubject: subject,
            upstreamState: new SubjectBoundOptions(this, subject)
        );
    }

    /// <inheritdoc />
    public async ValueTask<TModel> GetValueAsync(CancellationToken cancellationToken = default)
    {
        var result = await _resolution
            .ReadPublicValueAsync(cancellationToken)
            .ConfigureAwait(false);
        if (result.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException(
                $"Configuration state could not be read: {result.Status}."
            );
        }

        return result.Value!;
    }

    // Raw resolved read for internal callers and tests. The public read-side capability surface exposes
    // GetValueAsync, generated GetDetailsAsync, and Check instead.
    internal ValueTask<StateReadResult<TModel>> ReadAsync(
        CancellationToken cancellationToken = default
    ) => _resolution.ReadPublicValueAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<StateWriteReceipt> SaveAsync(
        IConfiglueModelPatch<TModel> patch,
        CancellationToken cancellationToken = default
    ) => _writes.SaveAsync(patch, cancellationToken);

    /// <inheritdoc />
    public ValueTask<StateWriteReceipt> ApplyPatchesAsync(
        IEnumerable<StateSourcePatch> patches,
        CancellationToken cancellationToken = default
    ) => _writes.ApplyPatchesAsync(patches, cancellationToken);

    /// <inheritdoc />
    public ValueTask<StateWritePreview> PreviewWriteAsync(
        TModel desired,
        CancellationToken cancellationToken = default
    ) => _writes.PreviewWriteAsync(desired, cancellationToken);

    /// <inheritdoc />
    public ConfiglueCheckOperation Check(CancellationToken cancellationToken = default) =>
        _inspection.CreateCheckOperation(subject: null, cancellationToken);

    internal ConfiglueCheckOperation CreateCheckOperation(
        IConfiglueSubject? subject,
        CancellationToken cancellationToken
    ) => _inspection.CreateCheckOperation(subject, cancellationToken);

    TModel IConfiglueValueCloneProvider<TModel>.CloneValue(TModel value) => _cloner.Clone(value);

    /// <inheritdoc />
    async ValueTask<ConfiglueDetailsSnapshot> IConfiglueDetailsRuntime.GetDetailsSnapshotAsync(
        CancellationToken cancellationToken
    ) => await _inspection.GetDetailsSnapshotAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    async ValueTask<StateSnapshot<TModel>> IConfiglueStateSnapshotRuntime<TModel>.GetSnapshotAsync(
        CancellationToken cancellationToken
    ) => await _inspection.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public ValueTask<EditSession<TModel>> OpenEditSessionAsync(
        CancellationToken cancellationToken = default
    ) =>
        _edits.OpenEditSessionCoreAsync(
            null,
            cancellationToken,
            pinnedSubject: null,
            upstreamState: this
        );

    /// <inheritdoc />
    public ValueTask<EditSession<TModel>> OpenEditSessionAsync(
        StateWritePlan writePlan,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        return _edits.OpenEditSessionCoreAsync(
            writePlan,
            cancellationToken,
            pinnedSubject: null,
            upstreamState: this
        );
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_lifetime.TryBeginShutdown(out _))
        {
            return;
        }

        _watches.ShutdownForDispose();
        _diagnostics.ClearListeners();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_lifetime.TryBeginShutdown(out var operationsDrained))
        {
            return;
        }

        var (watchTask, watcherTasks) = _watches.ShutdownForDispose();
        _diagnostics.ClearListeners();

        List<Exception>? errors = null;
        if (watchTask is not null)
        {
            try
            {
                await watchTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (errors ??= []).Add(exception);
            }
        }
        foreach (var watcherTask in watcherTasks)
        {
            try
            {
                await watcherTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (errors ??= []).Add(exception);
            }
        }
        try
        {
            await operationsDrained.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            (errors ??= []).Add(exception);
        }
        _watches.ReleaseShutdownResources();
        if (errors is not null)
        {
            throw new AggregateException("State shutdown failed.", errors);
        }
    }
}
