namespace Configlue;

/// <summary>A generated sparse fragment carrying Configlue schema metadata.</summary>
/// <typeparam name="TSelf">The generated fragment type.</typeparam>
public interface IConfiglueFragment<TSelf> : IConfiglueFragment, ISparseFragment<TSelf>
    where TSelf : class, IConfiglueFragment<TSelf> { }
