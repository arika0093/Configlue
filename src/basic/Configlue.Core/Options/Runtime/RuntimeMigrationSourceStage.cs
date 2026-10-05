using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// Phase 1+2 of storage migration: source selection, baseline capture, and
/// fragment/schema migration.
///
/// Owns the migration-only source registry (invisible to normal resolution),
/// orders selected sources by priority, and converts raw reads into
/// schema-migrated <see cref="MigrationSourceContribution{TModel,TFragment}"/>
/// values. Performs no target writes and no retirement.
/// </summary>
internal sealed class RuntimeMigrationSourceStage<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly RuntimeResolutionEngine<TModel, TFragment> _engine;
    private readonly RuntimeSourceTopology<TFragment> _topology;
    private readonly StateSource<TFragment>[] _migrationSources;
    private readonly Dictionary<SourceId, StateSource<TFragment>> _migrationSourceById;

    internal RuntimeMigrationSourceStage(
        RuntimeResolutionEngine<TModel, TFragment> engine,
        RuntimeSourceTopology<TFragment> topology,
        IEnumerable<StateSource<TFragment>>? migrationSources = null
    )
    {
        _engine = engine;
        _topology = topology;
        var declared = migrationSources?.ToArray() ?? [];
        if (declared.Any(static source => source is null))
        {
            throw new ArgumentException(
                "A migration-only source collection cannot contain null sources.",
                nameof(migrationSources)
            );
        }

        var duplicate = declared
            .GroupBy(static source => source.Id)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Migration-only source id '{duplicate.Key}' is registered more than once.",
                nameof(migrationSources)
            );
        }

        var colliding = declared.FirstOrDefault(source =>
            topology.SourceSet.Sources.Any(candidate => candidate.Id == source.Id)
        );
        if (colliding is not null)
        {
            throw new ArgumentException(
                $"Migration-only source '{colliding.Id}' is already registered as an active runtime source.",
                nameof(migrationSources)
            );
        }

        _migrationSources = declared;
        _migrationSourceById = declared.ToDictionary(
            static source => source.Id,
            static source => source
        );
    }

    /// <summary>Migration-only sources, invisible to normal resolution.</summary>
    internal IReadOnlyList<StateSource<TFragment>> MigrationSources => _migrationSources;

    /// <summary>
    /// Resolves a migration participant from the active topology first and from the
    /// migration-only definitions second.
    /// </summary>
    internal StateSource<TFragment> FindMigrationSource(SourceId sourceId)
    {
        var active = _topology.SourceSet.Sources.FirstOrDefault(candidate =>
            candidate.Id == sourceId
        );
        if (active is not null)
        {
            return active;
        }

        if (_migrationSourceById.TryGetValue(sourceId, out var migrationOnly))
        {
            return migrationOnly;
        }

        throw new InvalidOperationException($"State source '{sourceId}' is not registered.");
    }

    internal bool IsMigrationOnlySource(SourceId sourceId) =>
        _migrationSourceById.ContainsKey(sourceId);

    /// <summary>Validates endpoint ids and resolves the single-source migration pair.</summary>
    internal (StateSource<TFragment> Source, StateSource<TFragment> Target) ResolveSinglePair(
        SourceId sourceId,
        SourceId targetId
    )
    {
        if (sourceId.IsDefault || targetId.IsDefault)
        {
            throw new ArgumentException("Source IDs must be non-empty.");
        }

        return (FindMigrationSource(sourceId), FindMigrationSource(targetId));
    }

    internal void EnsureTargetWritable(StateSource<TFragment> target)
    {
        if (target.Writer is null)
        {
            throw new InvalidOperationException(
                $"State source '{target.Id}' does not support writes."
            );
        }

        if (!IsMigrationOnlySource(target.Id) && !_topology.IsSourceActive(target.Id))
        {
            throw new InvalidOperationException(
                $"State source '{target.Id}' has been retired from this state instance."
            );
        }
    }

    /// <summary>
    /// Validates a multi-source selection and returns participants ordered by
    /// priority (stable by request order).
    /// </summary>
    internal StateSource<TFragment>[] SelectOrderedSources(
        SourceId[] requestedSourceIds,
        IReadOnlyDictionary<SourceId, Func<TFragment, TFragment>> targetProjections,
        bool retireSources
    )
    {
        var selectedIds = requestedSourceIds.ToHashSet();
        var overlappingTarget = targetProjections.Keys.FirstOrDefault(selectedIds.Contains);
        if (!overlappingTarget.IsDefault)
        {
            throw new ArgumentException(
                $"Target '{overlappingTarget}' is also a selected source. Use MigrateSourceAsync for an in-place source migration.",
                nameof(targetProjections)
            );
        }

        if (retireSources)
        {
            var migrationOnlySelected = requestedSourceIds.FirstOrDefault(IsMigrationOnlySource);
            if (!migrationOnlySelected.IsDefault)
            {
                throw new InvalidOperationException(
                    $"Migration-only source '{migrationOnlySelected}' cannot be retired because it is not part of the active runtime topology."
                );
            }
        }

        return requestedSourceIds
            .Select((id, index) => (Source: FindMigrationSource(id), Index: index))
            .OrderByDescending(static item => item.Source.Priority)
            .ThenBy(static item => item.Index)
            .Select(static item => item.Source)
            .ToArray();
    }

    /// <summary>
    /// Reads one source and schema-migrates its fragment. Callers choose whether
    /// <see cref="StateReadStatus.NotFound"/> maps to the empty fragment (merge
    /// inputs) or fails (required single-source inputs).
    /// </summary>
    internal async ValueTask<MigrationSourceContribution<TModel, TFragment>> ReadContributionAsync(
        StateSource<TFragment> source,
        bool allowNotFound,
        CancellationToken cancellationToken
    )
    {
        var result = await _engine
            .ReadMigrationSourceAsync(source, cancellationToken)
            .ConfigureAwait(false);
        if (result.Status == StateReadStatus.Unavailable)
        {
            throw new InvalidOperationException(
                $"Source '{source.Id}' could not be migrated because it is unavailable."
            );
        }

        TFragment fragment = result.Status switch
        {
            StateReadStatus.NotFound when allowNotFound => RuntimeModel<
                TModel,
                TFragment
            >.EmptyFragment,
            StateReadStatus.Success => result.Value
                ?? throw new InvalidOperationException(
                    $"State source '{source.Id}' returned a null configuration fragment."
                ),
            _ => throw new InvalidOperationException(
                $"Source '{source.Id}' could not be migrated: {result.Status}."
            ),
        };
        if (result.Schema is { } schema)
        {
            fragment = await _engine
                .MigrateFragmentAsync(fragment, schema, cancellationToken)
                .ConfigureAwait(false);
        }

        return new MigrationSourceContribution<TModel, TFragment>(source, result, fragment);
    }

    /// <summary>Reads every ordered source; shared by single, legacy, and bulk flows.</summary>
    internal async ValueTask<MigrationBaseline<TModel, TFragment>> ReadBaselineAsync(
        StateSource<TFragment>[] orderedSources,
        CancellationToken cancellationToken
    )
    {
        var contributions = new List<MigrationSourceContribution<TModel, TFragment>>(
            orderedSources.Length
        );
        var revisions = new List<StateRevision>(orderedSources.Length);
        foreach (var source in orderedSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contribution = await ReadContributionAsync(
                    source,
                    allowNotFound: true,
                    cancellationToken
                )
                .ConfigureAwait(false);
            contributions.Add(contribution);
            revisions.Add(new StateRevision(source.Id, contribution.Result.Revision));
        }

        return new MigrationBaseline<TModel, TFragment>(contributions, revisions);
    }

    /// <summary>Pure merge: lowest-priority contribution wins the base, higher layers merge over it.</summary>
    internal static TFragment MergeContributions(
        IReadOnlyList<MigrationSourceContribution<TModel, TFragment>> contributions
    )
    {
        var merged = RuntimeModel<TModel, TFragment>.EmptyFragment;
        for (var index = contributions.Count - 1; index >= 0; index--)
        {
            merged = merged.Merge(contributions[index].Fragment);
        }

        return merged;
    }

    /// <summary>
    /// Whether two migration participants address the same physical resource (in-place
    /// representation replacement with old/new codecs over one backing store).
    /// </summary>
    internal bool SharesPhysicalResource(
        StateSource<TFragment> source,
        StateSource<TFragment> target
    )
    {
        if (ReferenceEquals(source, target))
        {
            return true;
        }

        var sourceId = _engine.GetResourceId(source);
        var targetId = _engine.GetResourceId(target);
        return sourceId is { IsDefault: false } left
            && targetId is { IsDefault: false } right
            && left == right;
    }
}
