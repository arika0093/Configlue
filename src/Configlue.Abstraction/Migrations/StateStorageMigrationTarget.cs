namespace Configlue;

/// <summary>Declares one projected target in a storage migration.</summary>
public sealed class StateStorageMigrationTarget<TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    /// <summary>Creates a migration target binding.</summary>
    public StateStorageMigrationTarget(string targetSourceId, Func<TFragment, TFragment> project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSourceId);
        ArgumentNullException.ThrowIfNull(project);
        TargetSourceId = targetSourceId;
        Project = project;
    }

    /// <summary>The registered writable source that receives this projection.</summary>
    public string TargetSourceId { get; }

    /// <summary>Projects the selected logical contribution into the target fragment.</summary>
    public Func<TFragment, TFragment> Project { get; }
}
