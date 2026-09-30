namespace Configlue.JsonSchema.MSBuild;

/// <summary>Inputs for a single schema-generation run.</summary>
public sealed class ConfiglueSchemaGenerationOptions
{
    /// <summary>The path to the built assembly that contains the Configlue models.</summary>
    public string AssemblyPath { get; init; } = string.Empty;

    /// <summary>The directory of the project being built, used for fallback output resolution.</summary>
    public string ProjectDirectory { get; init; } = string.Empty;

    /// <summary>An optional solution directory supplied by the build.</summary>
    public string? SolutionDirectory { get; init; }

    /// <summary>An explicit output directory. Empty uses the solution-root <c>schemas</c> directory.</summary>
    public string? OutputPath { get; init; }

    /// <summary>The persisted document layout described by the generated schema.</summary>
    public string DocumentLayout { get; init; } = "Simple";

    /// <summary>The property that carries the schema version in the Simple layout.</summary>
    public string VersionProperty { get; init; } = "$version";

    /// <summary>An optional absolute base URI applied to generated schema identifiers.</summary>
    public string? SchemaBaseUri { get; init; }
}
