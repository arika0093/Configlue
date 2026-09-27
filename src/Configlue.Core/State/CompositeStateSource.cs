using System.Linq;

namespace Configlue;

/// <summary>Combines sparse fragments from multiple physical sources as one logical read source.</summary>
/// <typeparam name="TFragment">The generated fragment type shared by the component sources.</typeparam>
/// <remarks>
/// Component sources are read in priority order and their present members are merged into one fragment. A
/// component's fallback condition determines whether a missing or unavailable component can be omitted. Writes
/// require an explicit default component or member routes and are expanded to component-local patches. All
/// successful components in one read must use the same schema metadata so schema migration can run once on the
/// combined fragment.
/// </remarks>
public sealed class CompositeStateSource<TFragment>
    : IStateReader<TFragment>,
        IStateWriter<TFragment>,
        IStateWatcher
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly StateSourceSet<TFragment> _components;
    private readonly string? _defaultWriteSourceId;
    private readonly StateWritePlan _writePlan;
    private WatchTarget[] _watchTargets = [];

    /// <summary>Creates a logical read source from priority-ordered component sources.</summary>
    /// <param name="components">Component sources that return the same generated fragment type.</param>
    /// <param name="defaultWriteSourceId">Optional component that owns members without an explicit route.</param>
    /// <param name="writePlan">Optional routes from top-level model member names to component source IDs.</param>
    public CompositeStateSource(
        StateSourceSet<TFragment> components,
        string? defaultWriteSourceId = null,
        StateWritePlan? writePlan = null
    )
    {
        ArgumentNullException.ThrowIfNull(components);
        _writePlan = writePlan ?? StateWritePlan.Empty;
        if (defaultWriteSourceId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(defaultWriteSourceId);
            GetWritableComponent(components, defaultWriteSourceId);
        }

        foreach (var (path, sourceId) in _writePlan.PropertyRoutes)
        {
            if (path.Contains('.', StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Composite write routes must target top-level model members.",
                    nameof(writePlan)
                );
            }

            GetWritableComponent(components, sourceId);
        }

        _defaultWriteSourceId = defaultWriteSourceId;

        _components = components;
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

    internal StateSource<TFragment> ResolveWriteComponent(string memberName)
    {
        var componentId = _writePlan.ResolveSourceId(
            memberName,
            _defaultWriteSourceId
                ?? throw new InvalidOperationException(
                    $"Composite member '{memberName}' has no configured write owner."
                )
        );
        return GetWritableComponent(_components, componentId);
    }

    internal async ValueTask<StateReadResult<TFragment>> ReadWithOverridesAsync(
        IReadOnlyDictionary<string, TFragment> overrides,
        CancellationToken cancellationToken
    ) => await ReadCoreAsync(overrides, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<StateReadResult<TFragment>> ReadAsync(
        CancellationToken cancellationToken = default
    ) => await ReadCoreAsync(null, cancellationToken).ConfigureAwait(false);

    private async ValueTask<StateReadResult<TFragment>> ReadCoreAsync(
        IReadOnlyDictionary<string, TFragment>? overrides,
        CancellationToken cancellationToken
    )
    {
        var successful = new List<ComponentResult>();
        var revisions = new List<StateRevision>(_components.Sources.Count);
        var nestedRevisions = new List<KeyValuePair<string, StateRevisionVector>>();
        var watchTargets = new List<WatchTarget>(_components.Sources.Count);
        StateReadResult<TFragment> lastFailure = default;
        StateSchemaMetadata? schema = null;
        var hasSchema = false;

        foreach (var source in _components.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
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
            watchTargets.Add(new WatchTarget(source, result.Revision));
            if (result.Revisions is { } nested)
            {
                nestedRevisions.Add(
                    new KeyValuePair<string, StateRevisionVector>(source.Id, nested)
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
                SetWatchTargets(watchTargets);
                return result with
                {
                    Revisions = new StateRevisionVector(revisions, nestedRevisions),
                };
            }
        }

        SetWatchTargets(watchTargets);
        if (successful.Count == 0)
        {
            return lastFailure with
            {
                Revisions = new StateRevisionVector(revisions, nestedRevisions),
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
            Revisions = new StateRevisionVector(revisions, nestedRevisions),
        };
    }

    /// <summary>Composite writes must be expressed as member patches so ownership stays component-local.</summary>
    public ValueTask<StateWriteResult> WriteAsync(
        StateWriteRequest<TFragment> request,
        CancellationToken cancellationToken = default
    )
    {
        _ = request;
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            "Composite sources accept member-routed patches; writing a merged fragment directly is unsafe."
        );
    }

    private static StateSource<TFragment> GetWritableComponent(
        StateSourceSet<TFragment> components,
        string componentId
    )
    {
        var component = components.Sources.FirstOrDefault(source =>
            string.Equals(source.Id, componentId, StringComparison.Ordinal)
        );
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
    public async ValueTask WaitForChangeAsync(
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        _ = observedRevision;
        var targets = Volatile.Read(ref _watchTargets);
        using var watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var watchers = new List<Task>(targets.Length);
        try
        {
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (target.Source.Watcher is not { } watcher)
                {
                    continue;
                }

                watchers.Add(
                    watcher.WaitForChangeAsync(target.Revision, watchCancellation.Token).AsTask()
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
            await watchCancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    private void SetWatchTargets(List<WatchTarget> targets) =>
        Volatile.Write(ref _watchTargets, targets.ToArray());

    private static string? GetPhysicalOrigin(List<ComponentResult> successful)
    {
        var origins = successful
            .Select(static component =>
                component.Result.PhysicalOrigin ?? component.Source.ResourceId?.Value
            )
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
        public WatchTarget(StateSource<TFragment> source, string? revision)
        {
            Source = source;
            Revision = revision;
        }

        public StateSource<TFragment> Source { get; }
        public string? Revision { get; }
    }
}
