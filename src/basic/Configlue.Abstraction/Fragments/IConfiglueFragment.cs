namespace Configlue;

/// <summary>Adds Configlue schema metadata to a generic sparse fragment.</summary>
public interface IConfiglueFragment : ISparseFragment
{
    /// <summary>Generated Configlue schema metadata for this fragment.</summary>
    ConfiglueModelSchema Schema { get; }
}
