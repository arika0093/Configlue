using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Configlue.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Configlue;

/// <summary>Resolves and saves a generated configuration model over a set of state sources.</summary>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
/// <typeparam name="TFragment">The model's generated sparse fragment.</typeparam>
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
    private static readonly ConcurrentDictionary<
        (Type ModelType, string MemberName),
        ValidationAttribute[]
    > MemberValidationAttributes = new();
    private static readonly ConcurrentDictionary<Type, bool> ModelValidationMetadata = new();
    private static readonly ConcurrentDictionary<Type, bool> MemberValidationMetadata = new();
    private static ConfiglueModelOperations<TModel, TFragment> ModelOperations =>
        ConfiglueModelOperations<TModel, TFragment>.Current;
    private static ConfiglueModelSchema ModelSchema => ModelOperations.Schema;
    private static TFragment EmptyFragment => ModelOperations.EmptyFragment;
    private static ConfiglueResourceContext DefaultResourceContext =>
        ConfiglueResourceContext.Default with
        {
            ModelId = ModelSchema.Id,
        };

    private static TFragment ToFragment(TModel value) => ModelOperations.ToFragment(value);

    private static TFragment Diff(TModel before, TModel after) =>
        ModelOperations.Diff(before, after);

    private static TModel FromFragment(TFragment value) => ModelOperations.FromFragment(value);

    private readonly StateSourceSet<TFragment> _sourceSet;
    private readonly TFragment _modelDefaultsFragment;
    private readonly StateSource<TFragment> _modelDefaultsSource;
    private readonly object _sourceGate = new();
    private readonly HashSet<SourceId> _retiredSourceIds = [];
    private readonly Dictionary<SourceId, string> _detailsSourceKeys = [];
    private StateSource<TFragment>[] _activeSources;
    private TaskCompletionSource _sourceTopologyChanged = NewTopologySignal();
    private readonly StateWritePlan _writePlan;
    private readonly bool _defaultWriteSourceIsInferred;
    private readonly Func<TModel, TModel>? _cloneStrategy;
    private readonly StateSchemaMigrationChain<TFragment> _migrationChain;
    private readonly IConfiglueValidator<TModel>[] _validators;
    private readonly string _stateName;
    private readonly bool _validateDataAnnotations;
    private readonly ReadValidationMode _readValidationMode;
    private readonly WriteConflictResolution _writeConflictResolution;
    private readonly TimeSpan _onChangeDebounce;
    private readonly TimeProvider _timeProvider;
    private readonly RuntimeDiagnosticRecorder _diagnostics;
    private readonly object _changeGate = new();
    private readonly AsyncLocal<IConfiglueSubject?> _subjectContext = new();
    private readonly ConcurrentDictionary<SubjectWatchSubscription, byte> _watcherOperations =
        new();
    private Func<Task>? _watcherCleanupBarrier;
    private readonly List<Action<TModel>> _changeListeners = [];
    private readonly List<Action<Exception>> _reloadFailureListeners = [];
    private readonly List<Action<StateRevisionVector?>> _reloadListeners = [];
    private CancellationTokenSource? _watchCancellation;
    private Task? _watchTask;
    private TaskCompletionSource? _operationsDrained;
    private int _activeOperations;
    private bool _disposed;

    RuntimeLifetimeRequirement IConfiglueRuntimeLifetimeProvider.RuntimeLifetime =>
        _sourceSet.RuntimeLifetime;

    /// <summary>
    /// Internal synchronization hook invoked immediately before a subject watcher removes itself
    /// from tracking and disposes its cancellation source. Tests use it to block the final
    /// lifetime cleanup while asserting that shutdown drains the owned completion.
    /// </summary>
    internal Func<Task>? WatcherCleanupBarrier
    {
        get => Volatile.Read(ref _watcherCleanupBarrier);
        set => Volatile.Write(ref _watcherCleanupBarrier, value);
    }

    /// <summary>Number of subject watchers whose complete lifetime has not yet been drained.</summary>
    internal int WatcherOperationCount => _watcherOperations.Count;

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
        TimeProvider? timeProvider = null
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
            timeProvider: timeProvider
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
        TimeProvider? timeProvider = null
    )
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        _sourceSet = sourceSet.WithModelId(ModelSchema.Id);
        _activeSources = _sourceSet.Sources.ToArray();
        _modelDefaultsFragment = ToFragment(FromFragment(EmptyFragment));
        _modelDefaultsSource = new StateSource<TFragment>(
            $"__configlue_model_defaults:{Guid.NewGuid():N}",
            new ModelDefaultsReader(_modelDefaultsFragment),
            new StateSourceOptions<TFragment>()
        );
        (_writePlan, _defaultWriteSourceIsInferred) = ResolveWriteOwnership(
            _activeSources,
            defaultWritePlan ?? StateWritePlan.Empty
        );
        _cloneStrategy = cloneStrategy;
        _validators = validators?.ToArray() ?? [];
        _stateName = stateName ?? string.Empty;
        _validateDataAnnotations = validateDataAnnotations;
        if (!Enum.IsDefined(readValidationMode))
        {
            throw new ArgumentOutOfRangeException(nameof(readValidationMode));
        }

        _readValidationMode = readValidationMode;
        if (!Enum.IsDefined(writeConflictResolution))
        {
            throw new ArgumentOutOfRangeException(nameof(writeConflictResolution));
        }

        _writeConflictResolution = writeConflictResolution;
        _onChangeDebounce = onChangeDebounce ?? TimeSpan.FromMilliseconds(300);
        if (_onChangeDebounce < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(onChangeDebounce),
                "Change debounce cannot be negative."
            );
        }

        _timeProvider = timeProvider ?? TimeProvider.System;

        if (_validators.Any(static validator => validator is null))
        {
            throw new ArgumentException(
                "Validators cannot contain null values.",
                nameof(validators)
            );
        }

        _migrationChain = new StateSchemaMigrationChain<TFragment>(
            ModelSchema.ToMetadata(),
            migrations
        );
        _diagnostics = new RuntimeDiagnosticRecorder(
            _stateName,
            ModelSchema.Id,
            ModelSchema.Version,
            diagnostics ?? ConfiglueRuntimeDiagnosticOptions.Default,
            _activeSources.Select(static source => new ConfiglueRuntimeSourceSnapshot(
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
            logger
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
        lock (_changeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _diagnostics.OnDiagnosticEvent(listener);
        }
    }

    private static (StateWritePlan Plan, bool DefaultInferred) ResolveWriteOwnership(
        IReadOnlyList<StateSource<TFragment>> sources,
        StateWritePlan configuredWritePlan
    )
    {
        var ownedPaths = new List<(string Path, SourceId SourceId)>();
        foreach (var source in sources)
        {
            if (source.Writer is null || source.ExplicitOnly)
            {
                continue;
            }

            foreach (var path in source.OwnedPropertyPaths)
            {
                ownedPaths.Add((path, source.Id));
            }
        }

        if (ownedPaths.Count > 1 && HasOverlappingOwnershipPaths(ownedPaths))
        {
            ThrowOverlappingOwnership(ownedPaths);
        }

        var owners = new Dictionary<string, SourceId>(StringComparer.Ordinal);
        foreach (var (path, sourceId) in ownedPaths)
        {
            owners.Add(path, sourceId);
        }

        var mountedWritePlan =
            owners.Count == 0 ? StateWritePlan.Empty : new StateWritePlan(null, owners);
        var merged = mountedWritePlan.OverrideWith(configuredWritePlan);

        var defaultSourceId = configuredWritePlan.DefaultSourceId;
        var defaultInferred = false;
        if (defaultSourceId is null)
        {
            StateSource<TFragment>? singleRoot = null;
            foreach (var source in sources)
            {
                if (
                    source.Writer is null
                    || source.ExplicitOnly
                    || source.OwnedPropertyPaths.Count > 0
                )
                {
                    continue;
                }

                if (singleRoot is not null)
                {
                    throw new InvalidOperationException(
                        $"Model '{typeof(TModel)}' has multiple writable root sources ('{singleRoot.Id}' and '{source.Id}') and no default write owner. "
                            + "Configure a default write owner with model.Writes(write => write.DefaultTo(...))."
                    );
                }

                singleRoot = source;
            }

            if (singleRoot is not null)
            {
                defaultSourceId = singleRoot.Id;
                defaultInferred = true;
            }
        }
        else if (!sources.Any(source => source.Id == defaultSourceId))
        {
            throw new InvalidOperationException(
                $"The configured default write source '{defaultSourceId}' is not registered for model '{typeof(TModel)}'."
            );
        }

        return (merged.WithDefaultSourceId(defaultSourceId).Bind(ModelSchema), defaultInferred);

        static bool HasOverlappingOwnershipPaths(List<(string Path, SourceId SourceId)> paths)
        {
            var sorted = new string[paths.Count];
            for (var index = 0; index < paths.Count; index++)
            {
                sorted[index] = paths[index].Path;
            }

            Array.Sort(sorted, static (first, second) => CompareOwnedPathOrder(first, second));
            for (var index = 1; index < sorted.Length; index++)
            {
                if (PathsOverlap(sorted[index - 1], sorted[index]))
                {
                    return true;
                }
            }

            return false;
        }

        static int CompareOwnedPathOrder(string first, string second)
        {
            var length = Math.Min(first.Length, second.Length);
            for (var index = 0; index < length; index++)
            {
                var comparison = OwnedPathCharOrder(first[index])
                    .CompareTo(OwnedPathCharOrder(second[index]));
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return first.Length.CompareTo(second.Length);
        }

        static int OwnedPathCharOrder(char value) => value == '.' ? 0 : value + 1;

        static bool PathsOverlap(string first, string second) =>
            string.Equals(first, second, StringComparison.Ordinal)
            || IsAncestorOwnedPath(first, second)
            || IsAncestorOwnedPath(second, first);

        static bool IsAncestorOwnedPath(string ancestor, string descendant) =>
            descendant.Length > ancestor.Length
            && descendant[ancestor.Length] == '.'
            && descendant.AsSpan(0, ancestor.Length).SequenceEqual(ancestor.AsSpan());

        static void ThrowOverlappingOwnership(List<(string Path, SourceId SourceId)> paths)
        {
            var seenOwners = new Dictionary<string, SourceId>(StringComparer.Ordinal);
            foreach (var (path, sourceId) in paths)
            {
                var existingOwner = seenOwners.FirstOrDefault(owner =>
                    string.Equals(owner.Key, path, StringComparison.Ordinal)
                    || owner.Key.StartsWith(path + ".", StringComparison.Ordinal)
                    || path.StartsWith(owner.Key + ".", StringComparison.Ordinal)
                );
                if (!string.IsNullOrEmpty(existingOwner.Key))
                {
                    throw new InvalidOperationException(
                        $"Writable sources '{existingOwner.Value}' and '{sourceId}' have overlapping ownership paths '{existingOwner.Key}' and '{path}'. Configure one source as explicit-only."
                    );
                }

                seenOwners.Add(path, sourceId);
            }
        }
    }

    /// <inheritdoc />
    public ConfiglueStateDiagnostics GetDiagnostics()
    {
        StateSource<TFragment>[] activeSources;
        lock (_sourceGate)
        {
            activeSources = _activeSources;
        }

        var activeIds = activeSources.Select(static source => source.Id).ToHashSet();
        var sources = _sourceSet
            .Sources.Select(source => new ConfiglueSourceDiagnostics(
                source.Id,
                source.Priority,
                source.FallbackCondition,
                canRead: true,
                canWrite: source.Writer is not null,
                canWatch: source.Watcher is not null,
                isActive: activeIds.Contains(source.Id),
                physicalOrigin: source.PhysicalOrigin,
                fixedResourceId: source.FixedResourceId
            ))
            .ToArray();
        return new ConfiglueStateDiagnostics(
            _stateName,
            sources,
            _writePlan.DefaultSourceId,
            _defaultWriteSourceIsInferred,
            _writePlan.PropertyRoutes
        );
    }

    /// <inheritdoc />
    public IDisposable OnChange(Action<TModel> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_changeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _changeListeners.Add(listener);
            EnsureWatcherStarted();
        }

        return new ChangeSubscription(this, listener);
    }

    /// <inheritdoc />
    public IDisposable OnReload(Action<StateRevisionVector?> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_changeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _reloadListeners.Add(listener);
            EnsureWatcherStarted();
        }

        return new ReloadSubscription(this, listener);
    }

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
        using var scope = EnterSubject(subject);
        return await GetValueAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<StateWriteReceipt> SaveForSubjectAsync(
        IConfiglueSubject subject,
        IConfiglueModelPatch<TModel> patch,
        CancellationToken cancellationToken
    )
    {
        using var scope = EnterSubject(subject);
        return await SaveAsync(patch, cancellationToken).ConfigureAwait(false);
    }

    private IDisposable WatchSubject(IConfiglueSubject subject, Action<TModel> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_changeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var subscription = new SubjectWatchSubscription(this, subject, listener);
            _watcherOperations.TryAdd(subscription, 0);
            subscription.Start();
            return subscription;
        }
    }

    private IDisposable EnterSubject(IConfiglueSubject subject)
    {
        var previous = _subjectContext.Value;
        _subjectContext.Value = subject;
        return new SubjectContextScope(_subjectContext, previous);
    }

    private ValueTask<StateReadResult<TFragment>> ReadSourceAsync(
        StateSource<TFragment> source,
        CancellationToken cancellationToken
    ) => ReadSourceAsync(source, GetResourceContext(source), cancellationToken);

    private ValueTask<StateReadResult<TFragment>> ReadSourceAsync(
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
            ? source.ReadAsync(context ?? DefaultResourceContext, cancellationToken)
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
                .ReadAsync(context ?? DefaultResourceContext, cancellationToken)
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

    private ValueTask<StateWriteResult> WriteSourceAsync(
        StateSource<TFragment> source,
        StateWriteRequest<TFragment> request,
        CancellationToken cancellationToken
    ) => WriteObservedAsync(source, source.Writer!, request, cancellationToken);

    private async ValueTask WaitForSourceChangeAsync(
        StateSource<TFragment> source,
        string? revision,
        CancellationToken cancellationToken
    )
    {
        var diagnostic = _diagnostics.Start(ConfiglueDiagnosticEventKind.WatchStarted, source.Id);
        Exception? failure = null;
        try
        {
            await source
                .WaitForChangeAsync(GetResourceContext(source), revision, cancellationToken)
                .ConfigureAwait(false);
            _diagnostics.Record(
                ConfiglueDiagnosticEventKind.WatchSignaled,
                diagnostic.Id,
                sourceId: source.Id
            );
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            if (failure is null)
            {
                diagnostic.Complete(ConfiglueDiagnosticEventKind.WatchStopped);
            }
            else
            {
                diagnostic.Fail(
                    ConfiglueDiagnosticEventKind.WatchStopped,
                    failure,
                    failure is OperationCanceledException
                        && cancellationToken.IsCancellationRequested
                );
            }
        }
    }

    private ConfiglueResourceContext GetResourceContext(StateSource<TFragment> source) =>
        _subjectContext.Value is { } subject
            ? source.GetResourceContext(subject)
            : DefaultResourceContext;

    private ResourceId? GetResourceId(StateSource<TFragment> source) =>
        source.GetResourceId(GetResourceContext(source));

    /// <inheritdoc />
    public IDisposable OnReloadFailed(Action<Exception> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_changeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _reloadFailureListeners.Add(listener);
            EnsureWatcherStarted();
        }

        return new ReloadFailureSubscription(this, listener);
    }

    /// <inheritdoc />
    public async ValueTask<TModel> GetValueAsync(CancellationToken cancellationToken = default)
    {
        var result = await ReadPublicValueAsync(cancellationToken).ConfigureAwait(false);
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
    ) => ReadPublicValueAsync(cancellationToken);

    private async ValueTask<StateReadResult<TModel>> ReadPublicValueAsync(
        CancellationToken cancellationToken,
        long parentOperationId = 0
    )
    {
        var result = await ReadCoreAsync(null, cancellationToken, parentOperationId)
            .ConfigureAwait(false);
        return
            _cloneStrategy is not null
            && result.Status == StateReadStatus.Success
            && result.Value is not null
            ? result.WithValue(CloneModel(result.Value))
            : result;
    }

    private async ValueTask<StateReadResult<TModel>> ReadCoreAsync(
        IReadOnlyDictionary<SourceId, StateReadResult<TFragment>>? replacements,
        CancellationToken cancellationToken,
        long parentOperationId = 0
    ) =>
        (
            await ResolveCoreAsync(
                    replacements,
                    cancellationToken,
                    parentOperationId: parentOperationId
                )
                .ConfigureAwait(false)
        ).Result;

    private ValueTask<ResolvedState> ResolveCoreAsync(
        IReadOnlyDictionary<SourceId, StateReadResult<TFragment>>? replacements,
        CancellationToken cancellationToken,
        bool captureContributions = false,
        Action<ResolvedSourceProbe>? observeSource = null,
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

    private async ValueTask<ResolvedState> ResolveObservedAsync(
        CancellationToken cancellationToken,
        bool captureContributions,
        Action<ResolvedSourceProbe>? observeSource,
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

    private async ValueTask<ResolvedState> ResolveImplementationAsync(
        IReadOnlyDictionary<SourceId, StateReadResult<TFragment>>? replacements,
        CancellationToken cancellationToken,
        bool captureContributions = false,
        Action<ResolvedSourceProbe>? observeSource = null,
        long operationId = 0
    )
    {
        using var operation = EnterOperation();
        var activeSources = GetActiveSources();
        var subject = _subjectContext.Value;
        List<ResolvedContribution>? contributions = captureContributions
            ? new List<ResolvedContribution>(activeSources.Length + 1)
            : null;
        TFragment[]? fragments =
            !captureContributions && activeSources.Length > 1
                ? ArrayPool<TFragment>.Shared.Rent(activeSources.Length + 1)
                : null;
        TFragment singleFragment = default!;
        List<ResolvedFailure>? failures = null;
        StateRevision[]? revisions =
            activeSources.Length > 1
                ? ArrayPool<StateRevision>.Shared.Rent(activeSources.Length)
                : null;
        var singleRevision = default(StateRevision);
        var revisionCount = 0;
        KeyValuePair<SourceId, StateRevisionVector>[]? nestedRevisions = null;
        var nestedRevisionCount = 0;
        StateReadResult<TFragment> lastFailure = default;
        StateSource<TFragment>? activeSource = null;
        StateReadResult<TFragment> activeResult = default;
        var successfulCount = 0;

        try
        {
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
                        : source.GetResourceId(DefaultResourceContext);
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
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    // Preserve the codec's original exception type so its recoverability policy can classify it.
#pragma warning disable S2139
                    catch (Exception exception)
                    {
                        observeSource?.Invoke(
                            new ResolvedSourceProbe
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
#pragma warning restore S2139
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
                    nestedRevisions ??= ArrayPool<
                        KeyValuePair<SourceId, StateRevisionVector>
                    >.Shared.Rent(activeSources.Length);
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
                        fragment = await MigrateAsync(
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
                    if (
                        replacements is null
                        && _readValidationMode == ReadValidationMode.StrictThrow
                    )
                    {
                        ValidateContribution(source, fragment);
                    }
                    else if (
                        replacements is null
                        && _readValidationMode == ReadValidationMode.IgnoreValue
                    )
                    {
                        var pruned = PruneInvalidMembers(source, fragment);
                        if (pruned is TFragment prunedFragment)
                        {
                            fragment = prunedFragment;
                        }
                    }

                    if (captureContributions)
                    {
                        contributions!.Add(
                            new ResolvedContribution(
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
                        new ResolvedSourceProbe
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
                var canFallBack = CanFallBack(source.FallbackCondition, result.Status);
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
                    new ResolvedSourceProbe
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
                            new ResolvedFailure(
                                source,
                                result,
                                ResourceContext: resourceContext,
                                ResourceId: resourceId
                            )
                        );
                    }

                    return new ResolvedState(
                        StateReadResult<TModel>.Create(
                            result.Status,
                            default,
                            result.Revision,
                            result.SourceId,
                            result.PhysicalOrigin,
                            result.Schema,
                            CreateRevisionVector(
                                revisions,
                                singleRevision,
                                revisionCount,
                                nestedRevisions,
                                nestedRevisionCount
                            )
                        ),
                        (IReadOnlyList<ResolvedContribution>?)contributions
                            ?? Array.Empty<ResolvedContribution>(),
                        null,
                        (IReadOnlyList<ResolvedFailure>?)failures ?? Array.Empty<ResolvedFailure>()
                    );
                }

                if (captureContributions)
                {
                    (failures ??= []).Add(
                        new ResolvedFailure(
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
                return new ResolvedState(
                    StateReadResult<TModel>.Create(
                        lastFailure.Status,
                        default,
                        lastFailure.Revision,
                        lastFailure.SourceId,
                        lastFailure.PhysicalOrigin,
                        lastFailure.Schema,
                        CreateRevisionVector(
                            revisions,
                            singleRevision,
                            revisionCount,
                            nestedRevisions,
                            nestedRevisionCount
                        )
                    ),
                    (IReadOnlyList<ResolvedContribution>?)contributions
                        ?? Array.Empty<ResolvedContribution>(),
                    null,
                    (IReadOnlyList<ResolvedFailure>?)failures ?? Array.Empty<ResolvedFailure>()
                );
            }

            if (captureContributions)
            {
                contributions!.Add(
                    new ResolvedContribution(
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

            var model = FromFragment(merged);
            if (replacements is null)
            {
                ValidateResolvedModel(model, merged);
            }
            var resolvedResult = StateReadResult<TModel>.Success(
                model,
                activeSource is null ? null : activeResult.Revision,
                ModelSchema.ToMetadata()
            ) with
            {
                SourceId = activeSource?.Id,
                PhysicalOrigin = activeSource is null ? null : activeResult.PhysicalOrigin,
                Revisions = CreateRevisionVector(
                    revisions,
                    singleRevision,
                    revisionCount,
                    nestedRevisions,
                    nestedRevisionCount
                ),
            };

            return new ResolvedState(
                resolvedResult,
                (IReadOnlyList<ResolvedContribution>?)contributions
                    ?? Array.Empty<ResolvedContribution>(),
                merged,
                (IReadOnlyList<ResolvedFailure>?)failures ?? Array.Empty<ResolvedFailure>()
            );
        }
        finally
        {
            if (fragments is not null)
            {
                ArrayPool<TFragment>.Shared.Return(fragments, clearArray: true);
            }

            if (revisions is not null)
            {
                ArrayPool<StateRevision>.Shared.Return(revisions, clearArray: true);
            }

            if (nestedRevisions is not null)
            {
                ArrayPool<KeyValuePair<SourceId, StateRevisionVector>>.Shared.Return(
                    nestedRevisions,
                    clearArray: true
                );
            }
        }
    }

    private static StateRevisionVector CreateRevisionVector(
        StateRevision[]? revisions,
        StateRevision singleRevision,
        int revisionCount,
        KeyValuePair<SourceId, StateRevisionVector>[]? nestedRevisions,
        int nestedRevisionCount
    )
    {
        if (revisions is null)
        {
            if (revisionCount == 0)
            {
                return StateRevisionVector.FromSpan(ReadOnlySpan<StateRevision>.Empty);
            }

            var nested = nestedRevisionCount > 0 ? nestedRevisions![0].Value : null;
            return StateRevisionVector.FromSingle(singleRevision, nested);
        }

        return StateRevisionVector.FromSpan(
            revisions.AsSpan(0, revisionCount),
            nestedRevisions is null ? default : nestedRevisions.AsSpan(0, nestedRevisionCount)
        );
    }
}
