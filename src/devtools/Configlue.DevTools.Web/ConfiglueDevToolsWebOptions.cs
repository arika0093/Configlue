namespace Configlue.DevTools.Web;

/// <summary>
/// Options for the development-only DevTools web host.
/// </summary>
/// <remarks>
/// <para>Development-only. The host always binds to loopback; there is no option for
/// public binding. Non-loopback binding, if ever supported, requires explicit
/// configuration plus an application-supplied authorization policy (not implemented).</para>
/// <para>Per-host browser auto-open is a separate follow-up; the host remains usable with
/// auto-open disabled. <see cref="AutoOpenBrowser"/> is accepted but currently only
/// recorded; no process is launched by this package.</para>
/// </remarks>
public sealed class ConfiglueDevToolsWebOptions
{
    /// <summary>
    /// The loopback port to bind. Zero selects a free port assigned by the operating system.
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// The local session token required by browser clients. When null or empty, a random
    /// token is generated per host instance and exposed via the host.
    /// </summary>
    public string? SessionToken { get; set; }

    /// <summary>
    /// Reserved for per-host browser auto-open follow-ups. The host stays usable with
    /// auto-open disabled; this package never launches a browser process itself.
    /// </summary>
    public bool AutoOpenBrowser { get; set; }

#pragma warning disable S3928 // Validation reports the invalid options property.
    internal void Validate()
    {
        if (Port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Port),
                "The DevTools port must be 0-65535."
            );
        }
    }
#pragma warning restore S3928
}
