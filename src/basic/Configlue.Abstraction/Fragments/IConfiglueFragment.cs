namespace Configlue;

/// <summary>
/// A sparse fragment that also carries Configlue persistence/schema metadata.
/// </summary>
public interface IConfiglueFragment : ISparseFragment
{
    /// <summary>Generated Configlue persisted-schema metadata for this fragment.</summary>
    ConfiglueModelSchema ConfiglueSchema { get; }
}
