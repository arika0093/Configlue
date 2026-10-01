using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Runtime.InteropServices;
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
        IDisposable,
        IAsyncDisposable
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private static readonly EventId SourceReadEvent = new(1000, "SourceRead");
    private static readonly EventId SourceReadFailedEvent = new(1001, "SourceReadFailed");
    private static readonly EventId SourceFallbackEvent = new(1002, "SourceFallback");
    private static readonly EventId SourceSelectedEvent = new(1003, "SourceSelected");
    private static readonly EventId PhysicalWriteEvent = new(1010, "PhysicalWrite");
    private static readonly EventId PhysicalWriteFailedEvent = new(1011, "PhysicalWriteFailed");
    private static readonly EventId MigrationEvent = new(1020, "StorageMigration");
    private static readonly EventId ConflictEvent = new(1030, "WriteConflict");
    private static readonly EventId WatchFailureEvent = new(1040, "WatcherFailure");
    private static readonly EventId ListenerFailureEvent = new(1041, "ChangeListenerFailure");
    private static readonly EventId WatchReloadEvent = new(1042, "WatcherReload");
    private static readonly EventId ReloadFailureListenerEvent = new(
        1043,
        "ReloadFailureListenerFailure"
    );
    private static readonly EventId ReloadListenerEvent = new(1045, "ReloadListenerFailure");
    private static readonly EventId ReadValidationEvent = new(1044, "ReadValidation");
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
    private readonly HashSet<string> _retiredSourceIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _detailsSourceKeys = new(StringComparer.Ordinal);
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
    private readonly ILogger? _logger;
    private readonly object _changeGate = new();
    private readonly AsyncLocal<IConfiglueSubject?> _subjectContext = new();
    private readonly ConcurrentDictionary<SubjectWatchSubscription, byte> _subjectSubscriptions =
        new();
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
        WriteConflictResolution writeConflictResolution = WriteConflictResolution.FailOnConflict
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
            writeConflictResolution: writeConflictResolution
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
        WriteConflictResolution writeConflictResolution = WriteConflictResolution.FailOnConflict
    )
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        _sourceSet = sourceSet.WithModelId(ModelSchema.Id);
        _activeSources = _sourceSet.Sources.ToArray();
        _modelDefaultsFragment = ToFragment(FromFragment(EmptyFragment));
        _modelDefaultsSource = new StateSource<TFragment>(
            $"__configlue_model_defaults:{Guid.NewGuid():N}",
            new ModelDefaultsReader(_modelDefaultsFragment)
        );
        (_writePlan, _defaultWriteSourceIsInferred) = ResolveWriteOwnership(
            _activeSources,
            defaultWritePlan ?? StateWritePlan.Empty
        );
        _cloneStrategy = cloneStrategy;
        _validators = validators?.ToArray() ?? [];
        _stateName = stateName ?? string.Empty;
        _logger = logger;
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
    }

    private static (StateWritePlan Plan, bool DefaultInferred) ResolveWriteOwnership(
        IReadOnlyList<StateSource<TFragment>> sources,
        StateWritePlan configuredWritePlan
    )
    {
        var ownedPaths = new List<(string Path, string SourceId)>();
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

        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, sourceId) in ownedPaths)
        {
            owners.Add(path, sourceId);
        }

        var mountedWritePlan =
            owners.Count == 0 ? StateWritePlan.Empty : new StateWritePlan(owners);
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
        else if (
            !sources.Any(source =>
                string.Equals(source.Id, defaultSourceId, StringComparison.Ordinal)
            )
        )
        {
            throw new InvalidOperationException(
                $"The configured default write source '{defaultSourceId}' is not registered for model '{typeof(TModel)}'."
            );
        }

        return (merged.WithDefaultSourceId(defaultSourceId).Bind(ModelSchema), defaultInferred);

        static bool HasOverlappingOwnershipPaths(List<(string Path, string SourceId)> paths)
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

        static void ThrowOverlappingOwnership(List<(string Path, string SourceId)> paths)
        {
            var seenOwners = new Dictionary<string, string>(StringComparer.Ordinal);
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

        var activeIds = activeSources
            .Select(static source => source.Id)
            .ToHashSet(StringComparer.Ordinal);
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
                resourceId: source.ResourceId
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
        IConfigluePatch patch,
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
            _subjectSubscriptions.TryAdd(subscription, 0);
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
    ) => source.ReadAsync(GetResourceContext(source), cancellationToken);

    private static ValueTask<StateReadResult<TFragment>> ReadSourceAsync(
        StateSource<TFragment> source,
        ConfiglueResourceContext? context,
        CancellationToken cancellationToken
    ) => source.ReadAsync(context ?? DefaultResourceContext, cancellationToken);

    private ValueTask<StateWriteResult> WriteSourceAsync(
        StateSource<TFragment> source,
        StateWriteRequest<TFragment> request,
        CancellationToken cancellationToken
    ) => source.WriteAsync(GetResourceContext(source), request, cancellationToken);

    private ValueTask WaitForSourceChangeAsync(
        StateSource<TFragment> source,
        string? revision,
        CancellationToken cancellationToken
    ) => source.WaitForChangeAsync(GetResourceContext(source), revision, cancellationToken);

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
        CancellationToken cancellationToken
    )
    {
        var result = await ReadCoreAsync(null, cancellationToken).ConfigureAwait(false);
        return
            _cloneStrategy is not null
            && result.Status == StateReadStatus.Success
            && result.Value is not null
            ? result.WithValue(CloneModel(result.Value))
            : result;
    }

    private async ValueTask<StateReadResult<TModel>> ReadCoreAsync(
        IReadOnlyDictionary<string, StateReadResult<TFragment>>? replacements,
        CancellationToken cancellationToken
    ) => (await ResolveCoreAsync(replacements, cancellationToken).ConfigureAwait(false)).Result;

    private async ValueTask<ResolvedState> ResolveCoreAsync(
        IReadOnlyDictionary<string, StateReadResult<TFragment>>? replacements,
        CancellationToken cancellationToken,
        bool captureContributions = false,
        Action<ResolvedSourceProbe>? observeSource = null
    )
    {
        using var operation = EnterOperation();
        var activeSources = GetActiveSources();
        var subject = _subjectContext.Value;
        List<ResolvedContribution>? contributions = captureContributions
            ? new List<ResolvedContribution>(activeSources.Length + 1)
            : null;
        TFragment[]? fragments = captureContributions
            ? null
            : new TFragment[activeSources.Length + 1];
        List<ResolvedFailure>? failures = null;
        var revisions = new StateRevision[activeSources.Length];
        var revisionCount = 0;
        List<KeyValuePair<string, StateRevisionVector>>? nestedRevisions = null;
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
            var resourceId =
                (captureContributions || observeSource is not null) && resourceContext is not null
                    ? source.GetResourceId(resourceContext.Value)
                    : source.ResourceId;
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
                _logger?.LogTrace(
                    SourceReadEvent,
                    "Reading configuration source {SourceId} for {ModelType} state {StateName} at {PhysicalOrigin} ({ResourceId}).",
                    source.Id,
                    typeof(TModel).FullName,
                    _stateName,
                    source.PhysicalOrigin,
                    source.ResourceId?.Value
                );
                try
                {
                    sourceResult = await ReadSourceAsync(source, resourceContext, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                // Preserve the codec's original exception type so its recoverability policy can classify it.
#pragma warning disable S2139
                catch (Exception exception)
                {
                    _logger?.LogError(
                        SourceReadFailedEvent,
                        exception,
                        "Reading configuration source {SourceId} failed for {ModelType} state {StateName} at {PhysicalOrigin} ({ResourceId}).",
                        source.Id,
                        typeof(TModel).FullName,
                        _stateName,
                        source.PhysicalOrigin,
                        source.ResourceId?.Value
                    );
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
            _logger?.LogDebug(
                SourceReadEvent,
                "Configuration source {SourceId} returned {ReadStatus} for {ModelType} state {StateName} at {PhysicalOrigin} ({ResourceId}).",
                source.Id,
                result.Status,
                typeof(TModel).FullName,
                _stateName,
                source.PhysicalOrigin,
                source.ResourceId?.Value
            );
            revisions[revisionCount++] = new StateRevision(source.Id, result.Revision);
            if (sourceResult.Revisions is { } nestedVector)
            {
                (nestedRevisions ??= []).Add(
                    new KeyValuePair<string, StateRevisionVector>(source.Id, nestedVector)
                );
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
                    _logger?.LogInformation(
                        MigrationEvent,
                        "Migrating schema from source {SourceId} from {SourceModelId} version {SourceVersion} for {ModelType} state {StateName}.",
                        source.Id,
                        sourceSchema.ModelId,
                        sourceSchema.Version,
                        typeof(TModel).FullName,
                        _stateName
                    );
                    fragment = await MigrateAsync(fragment, sourceSchema, cancellationToken)
                        .ConfigureAwait(false);
                }

                // Read validation governs source state; proposal resolutions (replacements)
                // are validated by their write paths instead.
                if (replacements is null && _readValidationMode == ReadValidationMode.StrictThrow)
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
                    fragments![successfulCount] = fragment;
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
            _logger?.Log(
                result.Status == StateReadStatus.Unavailable ? LogLevel.Warning : LogLevel.Debug,
                SourceFallbackEvent,
                "Configuration source {SourceId} returned {ReadStatus}; fallback {FallbackAction} for {ModelType} state {StateName}.",
                source.Id,
                result.Status,
                canFallBack ? "continues" : "stops",
                typeof(TModel).FullName,
                _stateName
            );
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
                        CreateRevisionVector(revisions, revisionCount, nestedRevisions)
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
                    new StateRevisionVector(
                        revisions,
                        (IEnumerable<KeyValuePair<string, StateRevisionVector>>?)nestedRevisions
                            ?? Array.Empty<KeyValuePair<string, StateRevisionVector>>()
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
            fragments![successfulCount] = _modelDefaultsFragment;
        }

        var contributionCount = successfulCount + 1;
        var merged = captureContributions
            ? contributions![^1].Result.Value!
            : fragments![successfulCount];
        for (var index = contributionCount - 2; index >= 0; index--)
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
            Revisions = CreateRevisionVector(revisions, revisionCount, nestedRevisions),
        };
        if (activeSource is not null)
        {
            _logger?.LogDebug(
                SourceSelectedEvent,
                "Configuration source {SourceId} is the highest-priority contributor for {ModelType} state {StateName}.",
                activeSource.Id,
                typeof(TModel).FullName,
                _stateName
            );
        }

        return new ResolvedState(
            resolvedResult,
            (IReadOnlyList<ResolvedContribution>?)contributions
                ?? Array.Empty<ResolvedContribution>(),
            merged,
            (IReadOnlyList<ResolvedFailure>?)failures ?? Array.Empty<ResolvedFailure>()
        );
    }

    private static StateRevisionVector CreateRevisionVector(
        StateRevision[] revisions,
        int revisionCount,
        List<KeyValuePair<string, StateRevisionVector>>? nestedRevisions
    ) =>
        nestedRevisions is null
            ? StateRevisionVector.FromSpan(revisions.AsSpan(0, revisionCount))
            : StateRevisionVector.FromSpan(
                revisions.AsSpan(0, revisionCount),
#if NETSTANDARD
                nestedRevisions.ToArray()
#else
                CollectionsMarshal.AsSpan(nestedRevisions)
#endif
            );
}
