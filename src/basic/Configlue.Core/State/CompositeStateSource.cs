using System.Linq;
using System.Text;
using Configlue.CompilerServices;
using Configlue.Sources;

namespace Configlue.State;

/// <summary>Combines sparse fragments from multiple physical sources as one logical read source.</summary>
/// <typeparam name="TFragment">The generated fragment type shared by the component sources.</typeparam>
/// <remarks>
/// <para>
/// Read composition is the primary primitive: component sources are read in priority order and
/// their present members are merged into one fragment. A component's fallback condition determines
/// whether a missing or unavailable component can be omitted. All successful components in one read
/// must use the same schema metadata so schema migration can run once on the combined fragment.
/// </para>
/// <para>
/// Write routing is explicit metadata only: a default component and/or member routes select the
/// owning component per member. The runtime expands routed patches to component-local writes and
/// owns all optimistic-concurrency checks; this type never caches per-subject state.
/// </para>
/// <para>
/// Watch aggregation is stateless: <see cref="ReadAsync"/> returns a composite revision that encodes
/// the observed per-component revisions, and <see cref="WaitForChangeAsync"/> decodes that revision
/// and fans out to the components with freshly resolved effective contexts. Per-subject and
/// per-route residency lives in the runtime (resolver/watch loop) via the returned revision and
/// revision vector; this type retains no watch targets, leases, or eviction state (see issue #310).
/// The revision vector is flat: component revisions are reported as direct entries, and deeper
/// nested vectors from components are not propagated.
/// </para>
/// </remarks>
/// <remarks>Advanced composition SPI: combines component sources as one logical source.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class CompositeStateSource<TFragment>
    : ISourceReader<TFragment>,
        ISourceWriter<TFragment>,
        ISourceWatcher
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly StateSourceSet<TFragment> _components;
    private readonly SourceId? _defaultWriteSourceId;
    private readonly StateWritePlan _writePlan;
    private readonly object _boundPlanGate = new();
    private StateWritePlan? _boundWritePlan;
    private ConfiglueModelSchema? _boundSchema;

    /// <summary>Creates a logical read source from priority-ordered component sources.</summary>
    /// <param name="components">Component sources that return the same generated fragment type.</param>
    /// <param name="defaultWriteSourceId">Optional component that owns members without an explicit route.</param>
    /// <param name="writePlan">Optional routes from model member paths to component source IDs.</param>
    /// <remarks>When both specify a default owner, the explicit <paramref name="defaultWriteSourceId"/> wins over <see cref="StateWritePlan.DefaultSourceId"/>.</remarks>
    public CompositeStateSource(
        StateSourceSet<TFragment> components,
        SourceId? defaultWriteSourceId = null,
        StateWritePlan? writePlan = null
    )
    {
        ArgumentNullException.ThrowIfNull(components);
        _writePlan = writePlan ?? StateWritePlan.Empty;
        if (defaultWriteSourceId is { IsDefault: true })
        {
            throw new ArgumentException(
                "A default source ID must be non-empty.",
                nameof(defaultWriteSourceId)
            );
        }

        var effectiveDefault = defaultWriteSourceId ?? _writePlan.DefaultSourceId;
        if (effectiveDefault is { } defaultSourceId)
        {
            GetWritableComponent(components, defaultSourceId);
        }

        foreach (var sourceId in _writePlan.PropertyRoutes.Values)
        {
            GetWritableComponent(components, sourceId);
        }

        _defaultWriteSourceId = effectiveDefault;

        _components = components;
    }

    /// <summary>The physical component sources in read-priority order.</summary>
    public IReadOnlyList<StateSource<TFragment>> Components => _components.Sources;

    /// <summary>Creates one logical source backed by this composition.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
    public StateSource<TFragment> CreateSource(
        string id,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound
    ) =>
        new(
            id,
            this,
            new StateSourceOptions<TFragment>
            {
                Priority = priority,
                FallbackCondition = fallbackCondition,
                Writer = HasWriteRoutes ? this : null,
                DisableWriteCapability = !HasWriteRoutes,
                Watcher = this,
            }
        );

    internal bool HasWriteRoutes =>
        _defaultWriteSourceId is not null || _writePlan.PropertyRoutes.Count > 0;

    internal StateWritePlan WritePlan => _writePlan;

    internal SourceId? DefaultWriteSourceId => _defaultWriteSourceId;

    internal StateSource<TFragment> ResolveWriteComponent(string propertyPath)
    {
        var componentId =
            _writePlan.ResolveSourceIdOrNull(propertyPath, _defaultWriteSourceId)
            ?? throw new InvalidOperationException(
                $"Composite property '{propertyPath}' has no configured write owner."
            );
        return GetWritableComponent(_components, componentId);
    }

    internal StateSource<TFragment> ResolveWriteComponent(
        ConfiglueMemberPath path,
        StateWritePlan boundPlan
    )
    {
        ArgumentNullException.ThrowIfNull(boundPlan);
        var componentId =
            boundPlan.ResolveSourceIdOrNull(path, _defaultWriteSourceId)
            ?? throw new InvalidOperationException(
                $"Composite property '{path}' has no configured write owner."
            );
        return GetWritableComponent(_components, componentId);
    }

    internal bool HasWriteRouteBelow(string propertyPath) => _writePlan.HasRouteBelow(propertyPath);

    internal bool HasWriteRouteBelow(ConfiglueMemberPath path, StateWritePlan boundPlan)
    {
        ArgumentNullException.ThrowIfNull(boundPlan);
        return boundPlan.HasRouteBelow(path);
    }

    /// <summary>
    /// Binds the composite write plan to the generated root schema once and reuses the
    /// compiled route table. Binding parses each diagnostic route string a single time;
    /// normal per-member routing then stays in generated-ID space with no
    /// <c>string.Join</c>/<c>Split</c> round trips.
    /// </summary>
    internal StateWritePlan GetBoundWritePlan(ConfiglueModelSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var cached = Volatile.Read(ref _boundWritePlan);
        var cachedSchema = Volatile.Read(ref _boundSchema);
        if (
            cached is not null
            && cachedSchema is not null
            && ConfiglueMemberPath.Root(cachedSchema).SameRoot(ConfiglueMemberPath.Root(schema))
        )
        {
            return cached;
        }

        lock (_boundPlanGate)
        {
            if (
                _boundWritePlan is not null
                && _boundSchema is not null
                && ConfiglueMemberPath.Root(_boundSchema).SameRoot(ConfiglueMemberPath.Root(schema))
            )
            {
                return _boundWritePlan;
            }

            StateWritePlan bound;
            try
            {
                bound = _writePlan.Bind(schema);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException(
                    $"Composite write route does not match a model member path: {exception.Message}",
                    exception
                );
            }

            Volatile.Write(ref _boundWritePlan, bound);
            Volatile.Write(ref _boundSchema, schema);
            return bound;
        }
    }

    /// <inheritdoc />
    public async ValueTask<StateReadResult<TFragment>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        context = ConfiglueResourceContext.Normalize(context);
        return await ReadCoreAsync(ConfigurationSubject(context), context, cancellationToken)
            .ConfigureAwait(false);
    }

    private static IConfiglueSubject? ConfigurationSubject(ConfiglueResourceContext context) =>
        context.IsDefault ? null : context.Subject;

    private async ValueTask<StateReadResult<TFragment>> ReadCoreAsync(
        IConfiglueSubject? subject,
        ConfiglueResourceContext context,
        CancellationToken cancellationToken
    )
    {
        var successful = new List<ComponentResult>();
        var revisions = new List<StateRevision>(_components.Count);
        var observations = new List<ComponentObservation>(_components.Count);
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

            revisions.Add(new StateRevision(source.Id, result.Revision));
            observations.Add(
                new ComponentObservation(source.Id, result.Revision, effectiveContext)
            );

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

                successful.Add(new ComponentResult(source, result));
                continue;
            }

            lastFailure = result;
            if (!CanFallBack(source.FallbackCondition, result.Status))
            {
                return result with { Revisions = new StateRevisionVector(revisions) };
            }
        }

        if (successful.Count == 0)
        {
            return lastFailure with { Revisions = new StateRevisionVector(revisions) };
        }

        TFragment? combined = null;
        for (var index = successful.Count - 1; index >= 0; index--)
        {
            var fragment = successful[index].Result.Value!;
            combined = combined is null ? fragment : combined.Merge(fragment);
        }

        return StateReadResult<TFragment>.Success(
            combined!,
            EncodeComponentRevisions(observations),
            schema
        ) with
        {
            PhysicalOrigin = GetPhysicalOrigin(successful),
            Revisions = new StateRevisionVector(revisions),
        };
    }

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
        context = ConfiglueResourceContext.Normalize(context);
        var subject = context.IsDefault ? null : context.Subject;
        return WaitForChangeCoreAsync(subject, context, observedRevision, cancellationToken);
    }

    private async ValueTask WaitForChangeCoreAsync(
        IConfiglueSubject? subject,
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken
    )
    {
        var expected = DecodeComponentRevisions(observedRevision);
        using var watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var watchers = new List<Task>(_components.Count);
        try
        {
            for (var index = 0; index < _components.Count; index++)
            {
                var component = _components[index];
                cancellationToken.ThrowIfCancellationRequested();
                if (component.Watcher is null)
                {
                    continue;
                }

                string? componentRevision = null;
                ConfiglueResourceContext effectiveContext;
                if (
                    subject is not null
                    && expected is not null
                    && expected.TryGetValue(component.Id, out var observation)
                )
                {
                    componentRevision = observation.Revision;
                    effectiveContext = observation.ResolveEffectiveContext(subject);
                }
                else
                {
                    effectiveContext = subject is null
                        ? context
                        : component.GetResourceContext(subject);
                }

                watchers.Add(
                    component
                        .WaitForChangeAsync(
                            effectiveContext,
                            componentRevision,
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

    /// <summary>
    /// Encodes the observed per-component revisions and their effective contexts into the
    /// composite revision returned by <see cref="ReadAsync"/>. Base64 parts keep the encoding
    /// unambiguous; null revisions and model IDs are recorded as <c>-</c>. Returns null when
    /// there is nothing to encode.
    /// </summary>
    internal static string? EncodeComponentRevisions(
        IReadOnlyList<ComponentObservation> observations
    )
    {
        if (observations.Count == 0)
        {
            return null;
        }

        var parts = new string[observations.Count];
        for (var index = 0; index < observations.Count; index++)
        {
            var observation = observations[index];
            var encodedId = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(observation.SourceId.Value)
            );
            var encodedRevision = observation.Revision is null
                ? "-"
                : Convert.ToBase64String(Encoding.UTF8.GetBytes(observation.Revision));
            var encodedResource = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(observation.ResourceKey.Value)
            );
            var encodedRoute = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(observation.Route.Value)
            );
            var encodedModel = observation.ModelId is null
                ? "-"
                : Convert.ToBase64String(Encoding.UTF8.GetBytes(observation.ModelId));
            parts[index] = string.Join(
                ".",
                encodedId,
                encodedRevision,
                encodedResource,
                encodedRoute,
                encodedModel
            );
        }

        return string.Join(",", parts);
    }

    /// <summary>
    /// Decodes a composite revision back to per-component observations. Returns null when the
    /// observed revision is missing or was not produced by <see cref="EncodeComponentRevisions"/>
    /// (for example a failure revision or a value from an older version); callers fall back to
    /// freshly resolved contexts and null per-component revisions so the wait stays conservative.
    /// </summary>
    internal static Dictionary<SourceId, ComponentObservation>? DecodeComponentRevisions(
        string? compositeRevision
    )
    {
        if (compositeRevision is null || compositeRevision.Length == 0)
        {
            return null;
        }

        try
        {
            var entries = compositeRevision.Split(',');
            var decoded = new Dictionary<SourceId, ComponentObservation>(entries.Length);
            foreach (var entry in entries)
            {
                var segments = entry.Split('.');
                if (segments.Length != 5)
                {
                    return null;
                }

                var sourceId = SourceId.From(
                    Encoding.UTF8.GetString(Convert.FromBase64String(segments[0]))
                );
                string? revision =
                    segments[1] == "-"
                        ? null
                        : Encoding.UTF8.GetString(Convert.FromBase64String(segments[1]));
                var resourceKey = Encoding.UTF8.GetString(Convert.FromBase64String(segments[2]));
                var route = Encoding.UTF8.GetString(Convert.FromBase64String(segments[3]));
                string? modelId =
                    segments[4] == "-"
                        ? null
                        : Encoding.UTF8.GetString(Convert.FromBase64String(segments[4]));
                decoded[sourceId] = new ComponentObservation(
                    sourceId,
                    revision,
                    resourceKey,
                    route,
                    modelId
                );
            }

            return decoded;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>One component observation captured during a composite read.</summary>
    internal sealed class ComponentObservation
    {
        public ComponentObservation(
            SourceId sourceId,
            string? revision,
            ConfiglueResourceContext effectiveContext
        )
        {
            SourceId = sourceId;
            Revision = revision;
            ResourceKey = effectiveContext.ResourceKey;
            Route = effectiveContext.Route;
            ModelId = effectiveContext.ModelId;
        }

        internal ComponentObservation(
            SourceId sourceId,
            string? revision,
            string resourceKey,
            string route,
            string? modelId
        )
        {
            SourceId = sourceId;
            Revision = revision;
            ResourceKey = string.IsNullOrEmpty(resourceKey)
                ? ResourceKey.Default
                : ResourceKey.From(resourceKey);
            Route = string.IsNullOrEmpty(route) ? RouteKey.Default : RouteKey.From(route);
            ModelId = modelId;
        }

        public SourceId SourceId { get; }

        public string? Revision { get; }

        public string? ModelId { get; }

        public ResourceKey ResourceKey { get; }

        public RouteKey Route { get; }

        /// <summary>
        /// Rebuilds the captured effective context for the current subject. The subject key is
        /// stable while routing selectors may observe mutated subject state, so the captured
        /// resource key and route are reused instead of re-resolving them.
        /// </summary>
        public ConfiglueResourceContext ResolveEffectiveContext(IConfiglueSubject subject)
        {
            ArgumentNullException.ThrowIfNull(subject);
            return new ConfiglueResourceContext(ModelId, subject, ResourceKey, Route);
        }
    }

    /// <summary>
    /// Merges already-read component fragments in priority order without additional source I/O.
    /// Lower-priority components are merged first so higher-priority members win.
    /// </summary>
    internal TFragment MergeComponentFragments(IReadOnlyDictionary<SourceId, TFragment> fragments)
    {
        TFragment? combined = null;
        for (var index = _components.Count - 1; index >= 0; index--)
        {
            var component = _components[index];
            if (!fragments.TryGetValue(component.Id, out var fragment) || fragment is null)
            {
                continue;
            }

            combined = combined is null ? fragment : combined.Merge(fragment);
        }

        return combined
            ?? throw new InvalidOperationException(
                "No component fragments were supplied for the composite merge."
            );
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
}
