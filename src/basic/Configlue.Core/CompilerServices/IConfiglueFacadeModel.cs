namespace Configlue.CompilerServices;

/// <summary>Marks a generated configuration model with a registered portable runtime descriptor.</summary>
/// <typeparam name="TSelf">The generated model.</typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
#pragma warning disable S2326
public interface IConfiglueFacadeModel<TSelf> { }
#pragma warning restore S2326
