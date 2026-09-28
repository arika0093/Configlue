namespace Configlue.CompilerServices;

/// <summary>The single generated registration entry point for a closed model.</summary>
/// <typeparam name="TSelf">The generated model.</typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public interface IConfiglueFacadeModel<TSelf>
    where TSelf : IConfiglueFacadeModel<TSelf>
{
    /// <summary>The generated schema and closed runtime registration descriptor.</summary>
    static abstract ConfiglueModelDescriptor<TSelf> Descriptor { get; }
}
