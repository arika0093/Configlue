using Configlue.CompilerServices;

namespace Configlue;

/// <summary>Builds generated configuration details from single resolution snapshots.</summary>
internal sealed partial class ConfiglueRuntime<TModel, TFragment>
{
    /// <inheritdoc />
    async ValueTask<ConfiglueDetailsSnapshot> IConfiglueDetailsRuntime.GetDetailsSnapshotAsync(
        CancellationToken cancellationToken
    )
    {
        var resolved = await ResolveCoreAsync(null, cancellationToken, captureContributions: true)
            .ConfigureAwait(false);
        EnsureResolvable(resolved, "Configuration details");
        return BuildDetailsSnapshot(resolved);
    }

    /// <inheritdoc />
    async ValueTask<StateSnapshot<TModel>> IConfiglueStateSnapshotRuntime<TModel>.GetSnapshotAsync(
        CancellationToken cancellationToken
    )
    {
        var resolved = await ResolveCoreAsync(null, cancellationToken, captureContributions: true)
            .ConfigureAwait(false);
        EnsureResolvable(resolved, "Configuration snapshot");
        return new StateSnapshot<TModel>(resolved.Result.Value!, BuildDetailsSnapshot(resolved));
    }

    private static void EnsureResolvable(ResolvedState resolved, string description)
    {
        if (resolved.Result.Status != StateReadStatus.Success || resolved.Result.Value is null)
        {
            throw new InvalidOperationException(
                $"{description} could not be read: {resolved.Result.Status}."
            );
        }
    }

    private ConfiglueDetailsSnapshot BuildDetailsSnapshot(ResolvedState resolved)
    {
        var value = resolved.Result.Value!;
        var activeSources = GetActiveSources();
        var descriptors = new ConfigSourceDetails[activeSources.Length + 1];
        var fragments = new IConfiglueFragment?[activeSources.Length + 1];
        var statuses = new StateReadStatus[activeSources.Length + 1];
        var contributionsById = resolved.Contributions.ToDictionary(
            static contribution => contribution.Source.Id,
            static contribution => contribution,
            StringComparer.Ordinal
        );
        var failuresById = resolved.Failures.ToDictionary(
            static failure => failure.Source.Id,
            static failure => failure,
            StringComparer.Ordinal
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
        fragments[^1] = _modelDefaultsFragment;
        statuses[^1] = StateReadStatus.Success;

        var contributions = resolved.Contributions;
        return new ConfiglueDetailsSnapshot(
            ModelSchema,
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
        IReadOnlyList<ResolvedContribution> contributions
    )
    {
        var targetId = _writePlan.ResolveSourceIdOrNull(propertyPath);
        StateSource<TFragment>? target;
        if (targetId is not null)
        {
            target = GetActiveSources()
                .FirstOrDefault(source =>
                    string.Equals(source.Id, targetId, StringComparison.Ordinal)
                );
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

        var activeSources = GetActiveSources();
        var targetIndex = Array.FindIndex(
            activeSources,
            source => string.Equals(source.Id, target.Id, StringComparison.Ordinal)
        );
        foreach (var contribution in contributions)
        {
            if (
                contribution.Result.Value is not null
                && propertyPath.TryGetFragmentValue(contribution.Result.Value, out _)
            )
            {
                var contributionIndex = contribution.IsModelDefaults
                    ? activeSources.Length
                    : Array.FindIndex(
                        activeSources,
                        source =>
                            string.Equals(
                                source.Id,
                                contribution.Source.Id,
                                StringComparison.Ordinal
                            )
                    );
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
        IReadOnlyList<ResolvedContribution> contributions
    )
    {
        var effectiveValue = propertyPath.GetModelValue(value, out var member);
        var sourceContributions = new List<(string SourceId, object? Value)>();
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

        return CollectCollectionElementProvenance(member, effectiveValue, sourceContributions)
            .Select(provenance => new ConfigCollectionElementData(
                provenance.Index,
                provenance.Value,
                provenance.SourceIndices
            ))
            .ToArray();
    }

    private string GetDetailsSourceKey(string sourceId)
    {
        lock (_sourceGate)
        {
            if (!_detailsSourceKeys.TryGetValue(sourceId, out var key))
            {
                key = Guid.NewGuid().ToString("N");
                _detailsSourceKeys.Add(sourceId, key);
            }

            return key;
        }
    }

    private ConfigSourceDetails DescribeModelDefaults() =>
        new(
            GetDetailsSourceKey(_modelDefaultsSource.Id),
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
                    SubjectKey.Default,
                    RouteKey.Default,
                    resourceId,
                    origin
                );
        }

        var resolved = context.Value;
        return new ConfigSourceResolutionDetails(
            resolved.Subject.Key,
            resolved.Key,
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
        var key = GetDetailsSourceKey(source.Id);
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

        if (
            source.ResourceId?.Value.StartsWith("file:", StringComparison.Ordinal) == true
            && origin is not null
        )
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
