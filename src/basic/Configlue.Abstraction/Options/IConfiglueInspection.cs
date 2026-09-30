namespace Configlue;

/// <summary>Actively checks whether the configured state can currently be resolved.</summary>
/// <typeparam name="T">The configuration model.</typeparam>
// The model parameter identifies the check service in typed and keyed DI registrations.
#pragma warning disable S2326
public interface IConfiglueInspection<T>
{
    /// <summary>
    /// Starts one operational check that streams the sources evaluated by resolution and exposes one
    /// final state-level result.
    /// </summary>
    ConfiglueCheckOperation Check(CancellationToken cancellationToken = default);
}
#pragma warning restore S2326
