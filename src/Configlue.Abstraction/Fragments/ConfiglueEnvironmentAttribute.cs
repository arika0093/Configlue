namespace Configlue;

/// <summary>Maps a model property to a specific environment variable name.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class ConfiglueEnvironmentAttribute : Attribute
{
    /// <summary>Maps a model property to the specified environment variable.</summary>
    public ConfiglueEnvironmentAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>The environment variable name used for this property.</summary>
    public string Name { get; }
}
