namespace Configlue;

/// <summary>A generated patch explicitly assigned to one logical state source.</summary>
public sealed record StateSourcePatch
{
    /// <summary>Creates a source-local patch request.</summary>
    public StateSourcePatch(string sourceId, IConfigluePatch patch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(patch);
        SourceId = sourceId;
        Patch = patch;
    }

    /// <summary>The logical source receiving the patch.</summary>
    public string SourceId { get; }

    /// <summary>The generated set/unset patch to apply to that source's fragment.</summary>
    public IConfigluePatch Patch { get; }
}
