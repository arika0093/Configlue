namespace Configlue.Hosting.Godot;

/// <summary>Resolves persistent Godot user data through the engine's user:// mapping.</summary>
public sealed class GodotHostPaths : IConfiglueHostPaths
{
    private readonly string _root;

    /// <summary>Creates a profile for the running Godot project.</summary>
    public GodotHostPaths() => _root = global::Godot.ProjectSettings.GlobalizePath("user://");

    /// <inheritdoc />
    public bool TryResolve(
        ConfiglueStandardLocation location,
        string applicationId,
        out string directory
    )
    {
        directory = location switch
        {
            ConfiglueStandardLocation.UserGlobal => _root,
            ConfiglueStandardLocation.BackupRoot => Path.Combine(_root, "Configlue", "Backups"),
            _ => string.Empty,
        };
        return !string.IsNullOrWhiteSpace(directory);
    }
}

/// <summary>Configures host paths for a running Godot project.</summary>
public static class GodotConfiglueBuilderExtensions
{
    /// <summary>Selects user:// and its Configlue backup subdirectory; HostGlobal and Local are unsupported.</summary>
    /// <param name="builder">The context builder.</param>
    /// <returns>The configured builder.</returns>
    public static ConfiglueBuilder UseGodot(this ConfiglueBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseHostPaths(new GodotHostPaths());
    }
}
