using System.Linq;
using Configlue.Internal;
using Configlue.Sources;

namespace Configlue.State;

/// <summary>Combines sparse fragments from multiple physical sources as one logical read source.</summary>
/// <typeparam name="TFragment">The generated fragment type shared by the component sources.</typeparam>
/// <remarks>
/// Component sources are read in priority order and their present members are merged into one fragment. A
/// component's fallback condition determines whether a missing or unavailable component can be omitted. Writes
/// require an explicit default component or member routes and are expanded to component-local patches. All
/// successful components in one read must use the same schema metadata so schema migration can run once on the
/// combined fragment. Per-subject watch targets are retained in a bounded residency cache. A watch lease pins
/// its captured targets until all component watchers have drained; idle entries are evicted during later cache
/// access without allocating a cleanup task per subject.
/// </remarks>
public sealed class CompositeStateSource<TFragment>
    : ISourceReader<TFragment>,
        ISourceWriter<TFragment>,
        ISourceWatcher
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly StateSourceSet<TFragment> _components;
    private readonly SourceId? _defaultWriteSourceId;
    private readonly StateWritePlan _writePlan;
    private readonly ResidencyCache<
        (SubjectKey SubjectKey, RouteKey Route),
        CompositeWatchState
    > _watchTargets;

    /// <summary>Creates a logical read source from priority-ordered component sources.</summary>
    /// <param name="components">Component sources that return the same generated fragment type.</param>
    /// <param name="defaultWriteSourceId">Optional component that owns members without an explicit route.</param>
    /// <param name="writePlan">Optional routes from model member paths to component source IDs.</param>
    public CompositeStateSource(
        StateSourceSet<TFragment> components,
        SourceId? defaultWriteSourceId = null,
        StateWritePlan? writePlan = null
    )
        : this(components, defaultWriteSourceId, writePlan, TimeSpan.FromMinutes(5), 256) { }

    internal CompositeStateSource(
        StateSourceSet<TFragment> components,
        SourceId? defaultWriteSourceId,
        StateWritePlan? writePlan,
        TimeSpan watchTargetIdleTimeout,
        int watchTargetCapacity
    )
    {
        ArgumentNullException.ThrowIfNull(components);
        _writePlan = writePlan ?? StateWritePlan.Empty;
        if (defaultWriteSourceId is { } configuredSourceId)
        {
            if (configuredSourceId.IsDefault)
            {
                throw new ArgumentException(
                    "A default source ID must be non-empty.",
                    nameof(defaultWriteSourceId)
                );
            }
            GetWritableComponent(components, configuredSourceId);
        }

        foreach (var sourceId in _writePlan.PropertyRoutes.Values)
        {
            GetWritableComponent(components, sourceId);
        }

        _defaultWriteSourceId = defaultWriteSourceId;

        _components = components;
        _watchTargets = new ResidencyCache<
            (SubjectKey SubjectKey, RouteKey Route),
            CompositeWatchState
        >(
            static _ => new CompositeWatchState(),
            idleTimeout: watchTargetIdleTimeout,
            capacity: watchTargetCapacity
        );
    }

    /// <summary>The physical component sources in read-priority order.</summary>
    public IReadOnlyList<StateSource<TFragment>> Components => _components.Sources;

    /// <summary>Creates one logical source backed by this composition.</summary>
    public StateSource<TFragment> CreateSource(
        string id,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound
    ) =>
        new(
            id,
            this,
            priority,
            fallbackCondition,
            writer: HasWriteRoutes ? this : null,
            watcher: this
        );

    internal bool HasWriteRoutes =>
        _defaultWriteSourceId is not null || _writePlan.PropertyRoutes.Count > 0;

    internal StateWritePlan WritePlan => _writePlan;

    internal SourceId? DefaultWriteSourceId => _defaultWriteSourceId;

    internal int WatchTargetCount => _watchTargets.Count;

    internal Action? BeforeWatchLeaseReturn
    {
        get => _watchTargets.BeforeLeaseReturn;
        set => _watchTargets.BeforeLeaseReturn = value;
    }

    internal void EvictIdleWatchTargetsForTest() => _watchTargets.Trim();

    internal StateSource<TFragment> ResolveWriteComponent(string propertyPath)
    {
        var componentId = _writePlan.ResolveSourceId(
            propertyPath,
            _defaultWriteSourceId
                ?? throw new InvalidOperationException(
                    $"Composite property '{propertyPath}' has no configured write owner."
                )
        );
        return GetWritableComponent(_components, componentId);
    }

    internal bool HasWriteRouteBelow(string propertyPath) => _writePlan.HasRouteBelow(propertyPath);

    internal async ValueTask<StateReadResult<TFragment>> ReadWithOverridesAsync(
        IReadOnlyDictionary<SourceId, TFragment> overrides,
        ConfiglueResourceContext context,
        CancellationToken cancellationToken
    )
    {
        context = ConfiglueResourceContext.Normalize(context);
        return await ReadCoreAsync(
                overrides,
                ConfigurationSubject(context),
                context,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<StateReadResult<TFragment>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        context = ConfiglueResourceContext.Normalize(context);
        return await ReadCoreAsync(null, ConfigurationSubject(context), context, cancellationToken)
            .ConfigureAwait(false);
    }

    private static IConfiglueSubject? ConfigurationSubject(ConfiglueResourceContext context) =>
        context.IsDefault ? null : context.Subject;

    private async ValueTask<StateReadResult<TFragment>> ReadCoreAsync(
        IReadOnlyDictionary<SourceId, TFragment>? overrides,
        IConfiglueSubject? subject,
        ConfiglueResourceContext context,
        CancellationToken cancellationToken
    )
    {
        var successful = new List<ComponentResult>();
        var revisions = new List<StateRevision>(_components.Count);
        List<KeyValuePair<SourceId, StateRevisionVector>>? nestedRevisions = null;
        var watchTargets = new List<WatchTarget>(_components.Count);
        StateReadResult<TFragment> lastFailure = default;
        StateSchemaMetadata? schema = null;
        var hasSchema = false;

        for (var index = 0; index < _components.Count; index++)
        {
            var source = _components[index];
            cancellationToken.ThrowIfCancellationRequested();
            var effectiveContext = subject is null ? context : source.GetResourceContext(subject);
            var result = await source
                .ReadAsync(effectiveContext, cancellationToken)
                .ConfigureAwait(false);
            result = result.FromSource(source.Id, source.PhysicalOrigin);
            if (
                result.Status == StateReadStatus.NotFound
                && overrides is not null
                && overrides.TryGetValue(source.Id, out var addedReplacement)
            )
            {
                result = StateReadResult<TFragment>.Success(
                    addedReplacement,
                    result.Revision,
                    addedReplacement.Schema.ToMetadata()
                ) with
                {
                    PhysicalOrigin = result.PhysicalOrigin,
                    Revisions = result.Revisions,
                };
            }

            revisions.Add(new StateRevision(source.Id, result.Revision));
            watchTargets.Add(new WatchTarget(source, effectiveContext, result.Revision));
            if (result.Revisions is { } nested)
            {
                nestedRevisions ??= [];
                nestedRevisions.Add(
                    new KeyValuePair<SourceId, StateRevisionVector>(source.Id, nested)
                );
            }

            if (result.Status == StateReadStatus.Success)
            {
                if (result.Value is null)
                {
                    throw new InvalidOperationException(
                        $"Composite component '{source.Id}' returned a null fragment."
                    );
                }

                if (!hasSchema)
                {
                    schema = result.Schema;
                    hasSchema = true;
                }
                else if (schema != result.Schema)
                {
                    throw new InvalidOperationException(
                        "Composite state source components must return matching schema metadata."
                    );
                }

                if (overrides is not null && overrides.TryGetValue(source.Id, out var replacement))
                {
                    result = StateReadResult<TFragment>.Success(
                        replacement,
                        result.Revision,
                        replacement.Schema.ToMetadata()
                    ) with
                    {
                        PhysicalOrigin = result.PhysicalOrigin,
                        Revisions = result.Revisions,
                    };
                }

                successful.Add(new ComponentResult(source, result));
                continue;
            }

            lastFailure = result;
            if (!CanFallBack(source.FallbackCondition, result.Status))
            {
                SetWatchTargets(watchTargets, subject, context);
                return result with { Revisions = CreateRevisionVector(revisions, nestedRevisions) };
            }
        }

        SetWatchTargets(watchTargets, subject, context);
        if (successful.Count == 0)
        {
            return lastFailure with
            {
                Revisions = CreateRevisionVector(revisions, nestedRevisions),
            };
        }

        TFragment? combined = null;
        for (var index = successful.Count - 1; index >= 0; index--)
        {
            var fragment = successful[index].Result.Value!;
            combined = combined is null ? fragment : combined.Merge(fragment);
        }

        return StateReadResult<TFragment>.Success(combined, schema: schema) with
        {
            PhysicalOrigin = GetPhysicalOrigin(successful),
            Revisions = CreateRevisionVector(revisions, nestedRevisions),
        };
    }

    private static StateRevisionVector CreateRevisionVector(
        List<StateRevision> revisions,
        List<KeyValuePair<SourceId, StateRevisionVector>>? nestedRevisions
    ) =>
        nestedRevisions is null
            ? new StateRevisionVector(revisions)
            : new StateRevisionVector(revisions, nestedRevisions);

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<TFragment> request,
        CancellationToken cancellationToken = default
    )
    {
        _ = context;
        _ = request;
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            "Composite sources accept member-routed patches; writing a merged fragment directly is unsafe."
        );
    }

    private static StateSource<TFragment> GetWritableComponent(
        StateSourceSet<TFragment> components,
        SourceId componentId
    )
    {
        var component = components.Sources.FirstOrDefault(source => source.Id == componentId);
        if (component is null)
        {
            throw new ArgumentException(
                $"Composite write target '{componentId}' is not a component source."
            );
        }

        if (component.Writer is null)
        {
            throw new ArgumentException(
                $"Composite write target '{componentId}' does not support writes."
            );
        }

        return component;
    }

    /// <inheritdoc />
    public ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        _ = observedRevision;
        context = ConfiglueResourceContext.Normalize(context);
        var subject = context.IsDefault ? null : context.Subject;
        return WaitForChangeCoreAsync(subject, context, cancellationToken);
    }

    private async ValueTask WaitForChangeCoreAsync(
        IConfiglueSubject? subject,
        ConfiglueResourceContext context,
        CancellationToken cancellationToken
    )
    {
        var cacheKey = (subject?.Key ?? SubjectKey.Default, context.Route);
        using var watchTargetLease = _watchTargets.Acquire(cacheKey);
        var targets = watchTargetLease.Value.Targets;
        using var watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var watchers = new List<Task>(targets.Length);
        try
        {
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (target.Source.Watcher is null)
                {
                    continue;
                }

                watchers.Add(
                    target
                        .Source.WaitForChangeAsync(
                            target.EffectiveContext,
                            target.Revision,
                            watchCancellation.Token
                        )
                        .AsTask()
                );
            }

            if (watchers.Count == 0)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return;
            }

            var finished = await Task.WhenAny(watchers).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
        }
        finally
        {
            if (!watchCancellation.IsCancellationRequested)
            {
                await watchCancellation.CancelAsync().ConfigureAwait(false);
            }

            await AwaitWatchersAsync(watchers, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task AwaitWatchersAsync(
        IReadOnlyList<Task> watchers,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await Task.WhenAll(watchers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private void SetWatchTargets(
        List<WatchTarget> targets,
        IConfiglueSubject? subject,
        ConfiglueResourceContext context
    )
    {
        using var lease = _watchTargets.Acquire(
            (subject?.Key ?? SubjectKey.Default, context.Route)
        );
        lease.Value.SetTargets(targets.ToArray());
    }

    private static string? GetPhysicalOrigin(List<ComponentResult> successful)
    {
        var origins = successful
            .Select(static component => component.Result.PhysicalOrigin)
            .Where(static origin => !string.IsNullOrWhiteSpace(origin))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return origins.Length == 0 ? null : string.Join("; ", origins);
    }

    private static bool CanFallBack(StateFallbackCondition condition, StateReadStatus status) =>
        status switch
        {
            StateReadStatus.NotFound => (condition & StateFallbackCondition.NotFound) != 0,
            StateReadStatus.Unavailable => (condition & StateFallbackCondition.Unavailable) != 0,
            StateReadStatus.InvalidPayload => (condition & StateFallbackCondition.InvalidPayload)
                != 0,
            _ => false,
        };

    private sealed class ComponentResult
    {
        public ComponentResult(StateSource<TFragment> source, StateReadResult<TFragment> result)
        {
            Source = source;
            Result = result;
        }

        public StateSource<TFragment> Source { get; }
        public StateReadResult<TFragment> Result { get; }
    }

    private sealed class WatchTarget
    {
        public WatchTarget(
            StateSource<TFragment> source,
            ConfiglueResourceContext effectiveContext,
            string? revision
        )
        {
            Source = source;
            EffectiveContext = effectiveContext;
            Revision = revision;
        }

        public StateSource<TFragment> Source { get; }
        public ConfiglueResourceContext EffectiveContext { get; }
        public string? Revision { get; }
    }

    private sealed class CompositeWatchState : IDisposable
    {
        private WatchTarget[] _targets = [];

        public WatchTarget[] Targets => Volatile.Read(ref _targets);

        public void SetTargets(WatchTarget[] targets) => Volatile.Write(ref _targets, targets);

        public void Dispose() { }
    }
}
