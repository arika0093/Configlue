namespace Configlue;

/// <summary>Previews the write routing for a desired effective model without mutating anything.</summary>
/// <typeparam name="T">The configuration model.</typeparam>
/// <remarks>
/// Used by transports with atomicity requirements (such as RFC 5789 PATCH) to reject
/// multi-resource write plans before the first physical write.
/// Advanced application API for atomic transports.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueWritePreview<T>
{
    /// <summary>
    /// Computes the semantic diff between the current effective state and <paramref name="desired"/>,
    /// routes it to logical sources, and groups the result by physical resource identity.
    /// </summary>
    /// <param name="desired">The desired effective model.</param>
    /// <param name="cancellationToken">Cancels the preview.</param>
    /// <returns>The physical write count and atomicity of the planned write.</returns>
    ValueTask<StateWritePreview> PreviewWriteAsync(
        T desired,
        CancellationToken cancellationToken = default
    );
}
