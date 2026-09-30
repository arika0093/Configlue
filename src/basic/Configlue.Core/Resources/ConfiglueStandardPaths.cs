namespace Configlue.Resources;

/// <summary>Returns platform-standard directories for per-user application data.</summary>
public static class ConfiglueStandardPaths
{
    /// <summary>Resolves a standard location through a host profile and canonicalizes the result.</summary>
    public static string ResolveDirectory(
        IConfiglueHostPaths hostPaths,
        ConfiglueStandardLocation location,
        string applicationId = ""
    )
    {
        ArgumentNullException.ThrowIfNull(hostPaths);
        if (!Enum.IsDefined(location))
        {
            throw new ArgumentOutOfRangeException(nameof(location));
        }
        if (
            location is ConfiglueStandardLocation.HostGlobal or ConfiglueStandardLocation.UserGlobal
        )
        {
            ValidateApplicationId(applicationId);
        }

        if (!hostPaths.TryResolve(location, applicationId, out var directory))
        {
            throw new NotSupportedException(
                $"The active host does not support the '{location}' standard storage location."
            );
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return Path.GetFullPath(directory);
    }

    /// <summary>Gets the standard per-user save directory for the supplied application identifier.</summary>
    /// <remarks>
    /// Windows uses <c>%APPDATA%/&lt;applicationId&gt;</c>. macOS and Linux honor
    /// <c>XDG_CONFIG_HOME</c>; their defaults are <c>~/Library/Application Support</c> and
    /// <c>~/.config</c>, respectively. The application supplies the file name.
    /// </remarks>
    public static string GetStandardSaveDirectory(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        if (
            applicationId is "." or ".."
            || Path.IsPathRooted(applicationId)
            || applicationId.Contains('/')
            || applicationId.Contains('\\')
        )
        {
            throw new ArgumentException(
                "The application identifier must be a single path segment.",
                nameof(applicationId)
            );
        }

        string baseDirectory;
        if (OperatingSystem.IsWindows())
        {
            baseDirectory = GetWindowsConfigDirectory();
        }
        else if (OperatingSystem.IsMacOS())
        {
            baseDirectory = GetXdgConfigDirectory(
                Path.Combine(GetUserProfileDirectory(), "Library", "Application Support")
            );
        }
        else
        {
            baseDirectory = GetXdgConfigDirectory(
                Path.Combine(GetUserProfileDirectory(), ".config")
            );
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, applicationId));
    }

    /// <summary>Gets the persistent per-user state directory used for shared Configlue data.</summary>
    /// <remarks>
    /// Windows uses <c>%LOCALAPPDATA%</c>. macOS uses
    /// <c>~/Library/Application Support</c>. Linux honors <c>XDG_STATE_HOME</c> and otherwise uses
    /// <c>~/.local/state</c>.
    /// </remarks>
    public static string GetPersistentUserDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.GetFullPath(localAppData);
            }

            var specialFolder = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData
            );
            return string.IsNullOrWhiteSpace(specialFolder)
                ? throw new InvalidOperationException(
                    "The current user's local application data directory is unavailable."
                )
                : Path.GetFullPath(specialFolder);
        }

        var profile = GetUserProfileDirectory();
        if (OperatingSystem.IsMacOS())
        {
            return Path.GetFullPath(Path.Combine(profile, "Library", "Application Support"));
        }

        var xdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        return string.IsNullOrWhiteSpace(xdgStateHome)
            ? Path.GetFullPath(Path.Combine(profile, ".local", "state"))
            : Path.GetFullPath(xdgStateHome);
    }

    /// <summary>Gets the shared directory used for persistent cross-process lock sidecars.</summary>
    /// <remarks>
    /// The directory honors <c>XDG_RUNTIME_DIR</c> and <c>TMPDIR</c> before falling back to
    /// <see cref="Path.GetTempPath"/>. Lock files are persistent markers, so they live under a
    /// <c>configlue/locks</c> subdirectory instead of beside the protected resource file.
    /// The lock file name embeds a hash of the protected path and the lock is held with exclusive
    /// sharing, so a pre-created file only causes contention, not access to the resource itself.
    /// </remarks>
#pragma warning disable S5443
    public static string GetSharedLockDirectory()
    {
        var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            runtimeDirectory = Environment.GetEnvironmentVariable("TMPDIR");
        }

        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            runtimeDirectory = Path.GetTempPath();
        }

        return Path.GetFullPath(Path.Combine(runtimeDirectory, "configlue", "locks"));
    }
#pragma warning restore S5443

    internal static bool TryResolveDefault(
        ConfiglueStandardLocation location,
        string applicationId,
        out string directory
    )
    {
        switch (location)
        {
            case ConfiglueStandardLocation.HostGlobal:
                string sharedBase;
                if (OperatingSystem.IsWindows())
                {
                    sharedBase = Environment.GetFolderPath(
                        Environment.SpecialFolder.CommonApplicationData
                    );
                }
                else if (OperatingSystem.IsMacOS())
                {
                    sharedBase = Path.Combine(
                        Path.DirectorySeparatorChar.ToString(),
                        "Library",
                        "Application Support"
                    );
                }
                else
                {
                    sharedBase = Path.Combine(Path.DirectorySeparatorChar.ToString(), "var", "lib");
                }
                if (string.IsNullOrWhiteSpace(sharedBase))
                {
                    directory = string.Empty;
                    return false;
                }

                directory = Path.Combine(sharedBase, applicationId);
                return true;
            case ConfiglueStandardLocation.UserGlobal:
                directory = GetStandardSaveDirectory(applicationId);
                return true;
            case ConfiglueStandardLocation.Local:
                directory = Environment.CurrentDirectory;
                return true;
            case ConfiglueStandardLocation.BackupRoot:
                directory = GetPersistentUserDataDirectory();
                return true;
            default:
                directory = string.Empty;
                return false;
        }
    }

    private static void ValidateApplicationId(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        if (
            applicationId is "." or ".."
            || Path.IsPathRooted(applicationId)
            || applicationId.Contains('/')
            || applicationId.Contains('\\')
        )
        {
            throw new ArgumentException(
                "The application identifier must be a single path segment.",
                nameof(applicationId)
            );
        }
    }

    private static string GetWindowsConfigDirectory()
    {
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrWhiteSpace(appData))
        {
            return appData;
        }

        var specialFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrWhiteSpace(specialFolder)
            ? throw new InvalidOperationException(
                "The current user's APPDATA directory is unavailable."
            )
            : specialFolder;
    }

    private static string GetXdgConfigDirectory(string fallback)
    {
        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return string.IsNullOrWhiteSpace(xdgConfigHome) ? fallback : xdgConfigHome;
    }

    private static string GetUserProfileDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            return profile;
        }

        return Environment.GetEnvironmentVariable("HOME")
            ?? throw new InvalidOperationException(
                "The current user's home directory is unavailable."
            );
    }
}
