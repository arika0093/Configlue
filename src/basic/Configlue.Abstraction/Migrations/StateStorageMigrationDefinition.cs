namespace Configlue.Migrations;

/// <summary>Declares a retryable migration from selected sources into projected targets.</summary>
public sealed class StateStorageMigrationDefinition<TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    /// <summary>Creates a storage migration definition.</summary>
    public StateStorageMigrationDefinition(
        string id,
        IEnumerable<SourceId> sourceIds,
        IEnumerable<StateStorageMigrationTarget<TFragment>> targets,
        bool retireSources = false
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(sourceIds);
        ArgumentNullException.ThrowIfNull(targets);
        var sourceIdArray = sourceIds.ToArray();
        var targetArray = targets.ToArray();
        if (
            sourceIdArray.Length == 0
            || sourceIdArray.Any(static sourceId => sourceId.IsDefault)
            || sourceIdArray.Distinct().Count() != sourceIdArray.Length
        )
        {
            throw new ArgumentException(
                "At least one non-empty, unique source ID is required.",
                nameof(sourceIds)
            );
        }

        if (
            targetArray.Length == 0
            || targetArray.Any(static target => target is null)
            || targetArray.Select(static target => target.TargetSourceId).Distinct().Count()
                != targetArray.Length
        )
        {
            throw new ArgumentException(
                "At least one target with a unique source ID is required.",
                nameof(targets)
            );
        }

        if (targetArray.Any(target => sourceIdArray.Contains(target.TargetSourceId)))
        {
            throw new ArgumentException(
                "A target source cannot also be a selected migration source.",
                nameof(targets)
            );
        }

        Id = id;
        SourceIds = Array.AsReadOnly(sourceIdArray);
        Targets = Array.AsReadOnly(targetArray);
        RetireSources = retireSources;
    }

    /// <summary>A stable caller-chosen ID used to persist migration progress.</summary>
    public string Id { get; }

    /// <summary>Selected source IDs whose contributions are migrated.</summary>
    public IReadOnlyList<SourceId> SourceIds { get; }

    /// <summary>Projected writable targets in migration order.</summary>
    public IReadOnlyList<StateStorageMigrationTarget<TFragment>> Targets { get; }

    /// <summary>Whether selected sources are retired after all targets are verified.</summary>
    public bool RetireSources { get; }
}
