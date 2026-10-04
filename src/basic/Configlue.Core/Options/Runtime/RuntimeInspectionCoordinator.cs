using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// Owns read-only inspection for one runtime: health checks, details snapshots,
/// state snapshots, and static diagnostics.
///
/// Composes resolution, topology, and write-plan state without owning them; all
/// inputs arrive as explicit resolution snapshots or coordinator references.
/// </summary>
internal sealed class RuntimeInspectionCoordinator<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly RuntimeResolutionEngine<TModel, TFragment> _engine;
    private readonly RuntimeSourceTopology<TFragment> _topology;
    private readonly RuntimeWriteCoordinator<TModel, TFragment> _writes;
    private readonly RuntimeSubjectContext _subjects;
    private readonly string _stateName;

    internal RuntimeInspectionCoordinator(
        RuntimeResolutionEngine<TModel, TFragment> engine,
        RuntimeSourceTopology<TFragment> topology,
        RuntimeWriteCoordinator<TModel, TFragment> writes,
        RuntimeSubjectContext subjects,
        string stateName
    )
    {
        _engine = engine;
        _topology = topology;
        _writes = writes;
        _subjects = subjects;
        _stateName = stateName;
    }

    internal ConfiglueStateDiagnostics GetDiagnostics()
    {
        var activeSources = _topology.GetActiveSources();

        var activeIds = activeSources.Select(static source => source.Id).ToHashSet();
        var sources = _topology
            .SourceSet.Sources.Select(source => new ConfiglueSourceDiagnostics(
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
            _writes.Plan.DefaultSourceId,
            _writes.DefaultWriteSourceIsInferred,
            _writes.Plan.PropertyRoutes
        );
    }

    internal ConfiglueCheckOperation CreateCheckOperation(
        IConfiglueSubject? subject,
        CancellationToken cancellationToken
    ) =>
        new(
            (reportSource, token) =>
                subject is null
                    ? RunCheckAsync(reportSource, token)
                    : RunCheckForSubjectAsync(subject, reportSource, token),
            cancellationToken
        );

    private async Task<ConfiglueCheckResult> RunCheckForSubjectAsync(
        IConfiglueSubject subject,
        Action<ConfiglueSourceCheckResult> reportSource,
        CancellationToken cancellationToken
    )
    {
        using var scope = _subjects.Enter(subject);
        return await RunCheckAsync(reportSource, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConfiglueCheckResult> RunCheckAsync(
        Action<ConfiglueSourceCheckResult> reportSource,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(reportSource);
        try
        {
            var resolved = await _engine
                .ResolveAsync(
                    null,
                    cancellationToken,
                    captureContributions: false,
                    observeSource: probe => reportSource(CreateSourceCheckResult(probe))
                )
                .ConfigureAwait(false);
            return CreateCheckResult(resolved.Result);
        }
        catch (ConfiglueValidationException exception)
        {
            return ConfiglueCheckResult.Invalid(exception.Failures);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ConfiglueCheckResult.Faulted(exception);
        }
    }

    private ConfiglueSourceCheckResult CreateSourceCheckResult(ResolvedSourceProbe<TFragment> probe)
    {
        var origin = probe.Result.PhysicalOrigin ?? probe.Source.PhysicalOrigin;
        var details = DescribeSource(
            probe.Source,
            origin,
            DescribeResolution(probe.ResourceContext, probe.ResourceId, origin)
        );
        return new ConfiglueSourceCheckResult(
            details,
            MapCheckStatus(probe.Result.Status, probe.Exception),
            probe.Contributed,
            probe.FallbackContinued,
            exception: probe.Exception
        );
    }

    private static ConfiglueCheckStatus MapCheckStatus(
        StateReadStatus status,
        Exception? exception
    ) =>
        exception is not null
            ? ConfiglueCheckStatus.Faulted
            : status switch
            {
                StateReadStatus.Success => ConfiglueCheckStatus.Success,
                StateReadStatus.NotFound => ConfiglueCheckStatus.NotFound,
                StateReadStatus.Unavailable => ConfiglueCheckStatus.Unavailable,
                StateReadStatus.InvalidPayload => ConfiglueCheckStatus.Invalid,
                _ => ConfiglueCheckStatus.Faulted,
            };

    private static ConfiglueCheckResult CreateCheckResult(StateReadResult<TModel> result) =>
        result.Status switch
        {
            StateReadStatus.Success => ConfiglueCheckResult.Resolved(),
            StateReadStatus.NotFound => ConfiglueCheckResult.NotFound(),
            StateReadStatus.Unavailable => ConfiglueCheckResult.Unavailable(),
            StateReadStatus.InvalidPayload => ConfiglueCheckResult.Invalid(),
            _ => ConfiglueCheckResult.Faulted(
                new InvalidOperationException(
                    $"Configuration state check produced an unexpected status '{result.Status}'."
                )
            ),
        };

    internal async ValueTask<ConfiglueDetailsSnapshot> GetDetailsSnapshotAsync(
        CancellationToken cancellationToken
    )
    {
        var resolved = await _engine
            .ResolveAsync(null, cancellationToken, captureContributions: true)
            .ConfigureAwait(false);
        EnsureResolvable(resolved, "Configuration details");
        return BuildDetailsSnapshot(resolved);
    }

    internal async ValueTask<StateSnapshot<TModel>> GetSnapshotAsync(
        CancellationToken cancellationToken
    )
    {
        var resolved = await _engine
            .ResolveAsync(null, cancellationToken, captureContributions: true)
            .ConfigureAwait(false);
        EnsureResolvable(resolved, "Configuration snapshot");
        return new StateSnapshot<TModel>(resolved.Result.Value!, BuildDetailsSnapshot(resolved));
    }

    private static void EnsureResolvable(
        ResolvedState<TModel, TFragment> resolved,
        string description
    )
    {
        if (resolved.Result.Status != StateReadStatus.Success || resolved.Result.Value is null)
        {
            throw new InvalidOperationException(
                $"{description} could not be read: {resolved.Result.Status}."
            );
        }
    }

    internal ConfiglueDetailsSnapshot BuildDetailsSnapshot(
        ResolvedState<TModel, TFragment> resolved
    )
    {
        var value = resolved.Result.Value!;
        var activeSources = _topology.GetActiveSources();
        var descriptors = new ConfigSourceDetails[activeSources.Length + 1];
        var fragments = new IConfiglueFragment?[activeSources.Length + 1];
        var statuses = new StateReadStatus[activeSources.Length + 1];
        var contributionsById = resolved.Contributions.ToDictionary(
            static contribution => contribution.Source.Id,
            static contribution => contribution,
            EqualityComparer<SourceId>.Default
        );
        var failuresById = resolved.Failures.ToDictionary(
            static failure => failure.Source.Id,
            static failure => failure,
            EqualityComparer<SourceId>.Default
        );
        for (var index = 0; index < activeSources.Length; index++)
        {
            var source = activeSources[index];
            if (contributionsById.TryGetValue(source.Id, out var contribution))
            {
                var origin = contribution.Result.PhysicalOrigin ?? source.PhysicalOrigin;
                fragments[index] = contribution.Result.Value;
                statuses[index] = StateReadStatus.Success;
                descriptors[index] = DescribeSource(
                    source,
                    origin,
                    DescribeResolution(
                        contribution.ResourceContext,
                        contribution.ResourceId,
                        origin
                    )
                );
            }
            else
            {
                if (failuresById.TryGetValue(source.Id, out var failure))
                {
                    var origin = failure.Result.PhysicalOrigin ?? source.PhysicalOrigin;
                    descriptors[index] = DescribeSource(
                        source,
                        origin,
                        DescribeResolution(failure.ResourceContext, failure.ResourceId, origin)
                    );
                    fragments[index] = null;
                    statuses[index] = failure.Result.Status;
                }
                else
                {
                    descriptors[index] = DescribeSource(source, source.PhysicalOrigin, null);
                    fragments[index] = null;
                    statuses[index] = StateReadStatus.NotFound;
                }
            }
        }

        descriptors[^1] = DescribeModelDefaults();
        fragments[^1] = _engine.ModelDefaultsFragment;
        statuses[^1] = StateReadStatus.Success;

        var contributions = resolved.Contributions;
        return new ConfiglueDetailsSnapshot(
            RuntimeModel<TModel, TFragment>.Schema,
            value,
            Array.AsReadOnly(descriptors),
            Array.AsReadOnly(fragments),
            Array.AsReadOnly(statuses),
            path => GetEditability(path, contributions),
            path => GetCollectionElementData(path, value, contributions)
        );
    }

    private ConfiglueEditability GetEditability(
        ConfiglueMemberPath propertyPath,
        IReadOnlyList<ResolvedContribution<TFragment>> contributions
    )
    {
        var targetId = _writes.Plan.ResolveSourceIdOrNull(propertyPath);
        StateSource<TFragment>? target;
        if (targetId is not null)
        {
            target = _topology.GetActiveSources().FirstOrDefault(source => source.Id == targetId);
            if (target is null)
            {
                return ConfiglueEditability.NoWriteTarget;
            }

            if (target.Writer is null)
            {
                return ConfiglueEditability.ReadOnly;
            }
        }
        else
        {
            return ConfiglueEditability.NoWriteTarget;
        }

        var member = propertyPath.ResolveMember();
        if (
            member.MergeStrategy is not null
            || member.MergeMode is MergeMode.Append or MergeMode.SetUnion or MergeMode.Custom
        )
        {
            return ConfiglueEditability.Editable;
        }

        var activeSources = _topology.GetActiveSources();
        var targetIndex = Array.FindIndex(activeSources, source => source.Id == target.Id);
        foreach (var contribution in contributions)
        {
            if (
                contribution.Result.Value is not null
                && propertyPath.TryGetFragmentValue(contribution.Result.Value, out _)
            )
            {
                var contributionIndex = contribution.IsModelDefaults
                    ? activeSources.Length
                    : Array.FindIndex(activeSources, source => source.Id == contribution.Source.Id);
                return contributionIndex < targetIndex
                    ? ConfiglueEditability.Shadowed
                    : ConfiglueEditability.Editable;
            }
        }

        return ConfiglueEditability.Editable;
    }

    private static IReadOnlyList<ConfigCollectionElementData> GetCollectionElementData(
        ConfiglueMemberPath propertyPath,
        TModel value,
        IReadOnlyList<ResolvedContribution<TFragment>> contributions
    )
    {
        var effectiveValue = propertyPath.GetModelValue(value, out var member);
        var sourceContributions = new List<(SourceId SourceId, object? Value)>();
        foreach (var contribution in contributions)
        {
            if (
                contribution.Result.Value is not null
                && propertyPath.TryGetFragmentValue(contribution.Result.Value, out var memberValue)
            )
            {
                sourceContributions.Add((contribution.Source.Id, memberValue));
            }
        }

        return ConfiglueMergeProvenance
            .ExplainElements(
                member,
                effectiveValue,
                sourceContributions
                    .Select(static contribution => (contribution.SourceId, contribution.Value))
                    .ToArray()
            )
            .Select(provenance => new ConfigCollectionElementData(
                provenance.Index,
                provenance.Value,
                provenance.SourceIndices
            ))
            .ToArray();
    }

    private ConfigSourceDetails DescribeModelDefaults() =>
        new(
            _topology.GetDetailsSourceKey(_engine.ModelDefaultsSourceId),
            "model-defaults",
            "Model defaults",
            null,
            canWrite: false,
            canWatch: false
        );

    private static ConfigSourceResolutionDetails? DescribeResolution(
        ConfiglueResourceContext? context,
        ResourceId? resourceId,
        string? origin
    )
    {
        if (context is null)
        {
            return resourceId is null && origin is null
                ? null
                : new ConfigSourceResolutionDetails(
                    SubjectKey.Default,
                    ResourceKey.Default,
                    RouteKey.Default,
                    resourceId,
                    origin
                );
        }

        var resolved = context.Value;
        return new ConfigSourceResolutionDetails(
            resolved.Subject.Key,
            resolved.ResourceKey,
            resolved.Route,
            resourceId,
            origin
        );
    }

    private ConfigSourceDetails DescribeSource<T>(
        StateSource<T> source,
        string? origin,
        ConfigSourceResolutionDetails? resolution
    )
    {
        var key = _topology.GetDetailsSourceKey(source.Id);
        if (origin is not null)
        {
            const string environmentPrefix = "environment:";
            if (
                origin.StartsWith(environmentPrefix, StringComparison.Ordinal)
                && origin.Length > environmentPrefix.Length
            )
            {
                var name = origin[environmentPrefix.Length..];
                return new ConfigSourceDetails(
                    key,
                    "Environment",
                    $"Environment ({name})",
                    origin,
                    source.Writer is not null,
                    source.Watcher is not null,
                    resolution
                );
            }

            const string commandLinePrefix = "command-line:";
            if (origin.StartsWith(commandLinePrefix, StringComparison.Ordinal))
            {
                return new ConfigSourceDetails(
                    key,
                    "CommandLine",
                    "Command Line",
                    origin,
                    source.Writer is not null,
                    source.Watcher is not null,
                    resolution
                );
            }

            if (
                origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            )
            {
                var host = Uri.TryCreate(origin, UriKind.Absolute, out var uri) ? uri.Host : origin;
                return new ConfigSourceDetails(
                    key,
                    "Http",
                    $"HTTP ({host})",
                    origin,
                    source.Writer is not null,
                    source.Watcher is not null,
                    resolution
                );
            }
        }

        if (origin is not null && Path.IsPathRooted(origin))
        {
            var fileName = Path.GetFileName(origin);
            return new ConfigSourceDetails(
                key,
                "File",
                string.IsNullOrEmpty(fileName) ? "File" : $"File ({fileName})",
                origin,
                source.Writer is not null,
                source.Watcher is not null,
                resolution
            );
        }

        return new ConfigSourceDetails(
            key,
            "Custom",
            string.IsNullOrEmpty(origin) ? "Custom"
#if NETSTANDARD2_0
                : origin!,
#else
                : origin,
#endif
            origin,
            source.Writer is not null,
            source.Watcher is not null,
            resolution
        );
    }
}
