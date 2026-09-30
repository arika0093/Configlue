namespace Configlue.CompilerServices;

/// <summary>Transports one resolution snapshot to generated typed details.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public interface IConfiglueDetailsRuntime
{
    /// <summary>Reads the generated details transport for one consistent resolution.</summary>
    ValueTask<ConfiglueDetailsSnapshot> GetDetailsSnapshotAsync(
        CancellationToken cancellationToken = default
    );
}
