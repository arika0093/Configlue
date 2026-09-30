namespace Configlue.Resources;

/// <summary>A standard application-data location supplied by the active host.</summary>
public enum ConfiglueStandardLocation
{
    /// <summary>Application data shared across users of the host when supported.</summary>
    HostGlobal,

    /// <summary>Persistent application data for the current platform user.</summary>
    UserGlobal,

    /// <summary>Application or deployment-local data.</summary>
    Local,

    /// <summary>The persistent root used for Configlue backup generations.</summary>
    BackupRoot,
}

/// <summary>Resolves standard storage locations using the active host's conventions.</summary>
public interface IConfiglueHostPaths
{
    /// <summary>Resolves a location, returning false when the host does not support it.</summary>
    /// <param name="location">The semantic storage location.</param>
    /// <param name="applicationId">The identity to append when the host location is not app-isolated.</param>
    /// <param name="directory">The resolved directory, when supported.</param>
    bool TryResolve(ConfiglueStandardLocation location, string applicationId, out string directory);
}
