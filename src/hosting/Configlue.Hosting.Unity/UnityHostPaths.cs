using UnityEngine;

namespace Configlue.Hosting.Unity;

/// <summary>Resolves Unity's application-specific persistent-data root.</summary>
public sealed class UnityHostPaths : IConfiglueHostPaths
{
    private readonly string _root;

    /// <summary>Creates a profile on Unity 6's initialized main thread.</summary>
    public UnityHostPaths()
    {
        UnityConfiglueDispatcher.EnsureMainThread();
        _root = Application.persistentDataPath;
    }

    /// <inheritdoc />
    public bool TryResolve(
        ConfiglueStandardLocation location,
        string applicationId,
        out string directory
    )
    {
        directory = string.Empty;
        if (string.IsNullOrWhiteSpace(_root))
            return false;
        directory = location switch
        {
            ConfiglueStandardLocation.UserGlobal => _root,
            ConfiglueStandardLocation.BackupRoot => Path.Combine(_root, "Configlue", "Backups"),
            _ => string.Empty,
        };
        return directory.Length != 0;
    }
}

/// <summary>Configures native Unity persistent-data storage.</summary>
public static class UnityConfiglueBuilderExtensions
{
    /// <summary>Selects persistentDataPath and its Configlue backup subdirectory; HostGlobal and Local remain unsupported.</summary>
    /// <param name="builder">The context builder configured on the initialized Unity main thread.</param>
    /// <returns>The configured builder.</returns>
    public static ConfiglueBuilder UseUnity(this ConfiglueBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseHostPaths(new UnityHostPaths());
    }
}
