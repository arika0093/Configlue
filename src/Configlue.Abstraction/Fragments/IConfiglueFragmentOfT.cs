namespace Configlue;

/// <summary>A generated fragment that can be combined with another contribution.</summary>
/// <typeparam name="TSelf">The generated fragment type.</typeparam>
public interface IConfiglueFragment<TSelf> : IConfiglueFragment
    where TSelf : class, IConfiglueFragment<TSelf>
{
    /// <summary>An empty fragment with no present members.</summary>
    static abstract TSelf Empty { get; }

    /// <summary>Merges a higher-priority contribution over this fragment.</summary>
    TSelf Merge(TSelf higher);
}
