namespace Configlue.Resource.Zip;

/// <summary>Configures subject-aware entry selection for a ZIP resource view.</summary>
public sealed class ZipEntryResourceOptions
{
    /// <summary>Resolves the entry path for each subject-aware operation.</summary>
    /// <remarks>Return the same path for a given context so reads and writes address one entry.</remarks>
    /// <remarks>When unset, the entry name supplied to the constructor is used for every subject.</remarks>
    public Func<ConfiglueResourceContext, string>? EntryNameSelector { get; init; }
}
