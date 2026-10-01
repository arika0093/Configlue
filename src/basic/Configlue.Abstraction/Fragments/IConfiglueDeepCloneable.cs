namespace Configlue;

/// <summary>Configlue compatibility marker for the generic sparse deep-clone contract.</summary>
/// <typeparam name="TSelf">The cloned type.</typeparam>
public interface IConfiglueDeepCloneable<TSelf> : ISparseDeepCloneable<TSelf> { }
