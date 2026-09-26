namespace Configlue;

/// <summary>Returns platform-standard directories for application configuration files.</summary>
public static class ConfiglueStandardPaths
{
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
