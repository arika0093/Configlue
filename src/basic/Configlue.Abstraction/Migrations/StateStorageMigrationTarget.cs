namespace Configlue.Migrations;

/// <summary>Declares one projected target in a storage migration.</summary>
/// <remarks>Advanced application API for storage evolution.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class StateStorageMigrationTarget<TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    /// <summary>Creates a migration target binding.</summary>
    public StateStorageMigrationTarget(SourceId targetSourceId, Func<TFragment, TFragment> project)
    {
        if (targetSourceId.IsDefault)
        {
            throw new ArgumentException(
                "The target source identifier is uninitialized.",
                nameof(targetSourceId)
            );
        }
        ArgumentNullException.ThrowIfNull(project);
        TargetSourceId = targetSourceId;
        Project = project;
    }

    /// <summary>The registered writable source that receives this projection.</summary>
    public SourceId TargetSourceId { get; }

    /// <summary>Projects the selected logical contribution into the target fragment.</summary>
    public Func<TFragment, TFragment> Project { get; }
}
