namespace Configlue.CompilerServices;

/// <summary>The static generated contract used by Configlue's typed runtime.</summary>
/// <typeparam name="TSelf">The configuration model.</typeparam>
/// <typeparam name="TFragment">The model's generated sparse fragment.</typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public interface IConfiglueModel<out TSelf, TFragment>
    : IConfiglueDeepCloneable<TSelf>,
        IConfiglueModel
    where TSelf : IConfiglueModel<TSelf, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment> { }

/// <summary>The generated schema identity needed by typed registration selectors.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public interface IConfiglueModel { }
