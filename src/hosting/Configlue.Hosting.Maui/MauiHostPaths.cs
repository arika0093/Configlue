using Microsoft.Maui.Storage;

namespace Configlue.Hosting.Maui;

/// <summary>Resolves app-isolated persistent MAUI storage without appending a second application identifier.</summary>
public sealed class MauiHostPaths : IConfiglueHostPaths
{
    private readonly IFileSystem _fileSystem;

    /// <summary>Creates the host profile over MAUI's filesystem service.</summary>
    /// <param name="fileSystem">The native service or an application-supplied implementation.</param>
    public MauiHostPaths(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
    }

    /// <inheritdoc />
    public bool TryResolve(
        ConfiglueStandardLocation location,
        string applicationId,
        out string directory
    )
    {
        directory = string.Empty;
        if (
            location
            is not (ConfiglueStandardLocation.UserGlobal or ConfiglueStandardLocation.BackupRoot)
        )
            return false;
        var root = _fileSystem.AppDataDirectory;
        if (string.IsNullOrWhiteSpace(root))
            return false;
        directory =
            location == ConfiglueStandardLocation.UserGlobal
                ? root
                : Path.Combine(root, "Configlue", "Backups");
        return true;
    }
}

/// <summary>Configures native MAUI application storage for a Configlue context.</summary>
public static class MauiConfiglueBuilderExtensions
{
    /// <summary>Selects MAUI app data and app-local backups; HostGlobal and Local remain unsupported.</summary>
    /// <param name="builder">The context builder.</param>
    /// <param name="fileSystem">An optional injected filesystem service; defaults to MAUI's current service.</param>
    /// <returns>The configured builder.</returns>
    public static ConfiglueBuilder UseMaui(
        this ConfiglueBuilder builder,
        IFileSystem? fileSystem = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseHostPaths(new MauiHostPaths(fileSystem ?? FileSystem.Current));
    }
}
