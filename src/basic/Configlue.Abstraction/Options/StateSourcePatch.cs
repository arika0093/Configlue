namespace Configlue;

/// <summary>A generated patch explicitly assigned to one logical state source.</summary>
public sealed record StateSourcePatch
{
    /// <summary>Creates a source-local patch request.</summary>
    public StateSourcePatch(SourceId sourceId, IConfigluePatch patch)
    {
        if (sourceId.IsDefault)
        {
            throw new ArgumentException(
                "The source identifier is uninitialized.",
                nameof(sourceId)
            );
        }
        ArgumentNullException.ThrowIfNull(patch);
        SourceId = sourceId;
        Patch = patch;
    }

    /// <summary>The logical source receiving the patch.</summary>
    public SourceId SourceId { get; }

    /// <summary>The generated set/unset patch to apply to that source's fragment.</summary>
    public IConfigluePatch Patch { get; }
}
