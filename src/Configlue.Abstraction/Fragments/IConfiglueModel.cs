namespace Configlue;

/// <summary>The static generated contract used by Configlue's typed runtime.</summary>
/// <typeparam name="TSelf">The configuration model.</typeparam>
/// <typeparam name="TFragment">The model's generated sparse fragment.</typeparam>
public interface IConfiglueModel<TSelf, TFragment> : IConfiglueDeepCloneable<TSelf>
    where TSelf : IConfiglueModel<TSelf, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    /// <summary>Generated model schema metadata.</summary>
    static abstract ConfiglueModelSchema ConfiglueSchema { get; }

    /// <summary>Creates a complete fragment from a model value.</summary>
    static abstract TFragment ToFragment(TSelf value);

    /// <summary>Creates a model value from a merged fragment.</summary>
    static abstract TSelf FromFragment(TFragment value);
}
