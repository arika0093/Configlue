using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Configlue.JsonSchema.MSBuild;

/// <summary>MSBuild task that generates Configlue JSON Schemas from a built assembly.</summary>
public sealed class GenerateConfiglueSchemas : Microsoft.Build.Utilities.Task
{
    /// <summary>Gets or sets the built assembly that contains the Configlue models.</summary>
    [Required]
    public string AssemblyPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the directory of the project being built.</summary>
    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional solution directory.</summary>
    public string? SolutionDirectory { get; set; }

    /// <summary>Gets or sets the optional explicit output path.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Gets or sets the persisted document layout (Simple or Detailed).</summary>
    public string DocumentLayout { get; set; } = "Simple";

    /// <summary>Gets or sets the version property name used by the Simple layout.</summary>
    public string VersionProperty { get; set; } = "$version";

    /// <summary>Gets or sets the optional absolute schema base URI.</summary>
    public string? SchemaBaseUri { get; set; }

    /// <summary>Gets or sets the target framework being generated.</summary>
    public string? TargetFramework { get; set; }

    /// <summary>Gets the full paths of schema files that were written.</summary>
    [Output]
    public ITaskItem[] WrittenFiles { get; private set; } = [];

    /// <summary>Gets the full paths of schema files that were already current.</summary>
    [Output]
    public ITaskItem[] UpToDateFiles { get; private set; } = [];

    /// <summary>Gets the resolved output directory.</summary>
    [Output]
    public string ResolvedOutputPath { get; private set; } = string.Empty;

    /// <inheritdoc />
    public override bool Execute()
    {
        var result = ConfiglueSchemaGenerator.Generate(
            new ConfiglueSchemaGenerationOptions
            {
                AssemblyPath = AssemblyPath,
                ProjectDirectory = ProjectDirectory,
                SolutionDirectory = SolutionDirectory,
                OutputPath = OutputPath,
                DocumentLayout = DocumentLayout,
                VersionProperty = VersionProperty,
                SchemaBaseUri = SchemaBaseUri,
            }
        );

        if (result.OutputDirectory is not null)
        {
            ResolvedOutputPath = result.OutputDirectory;
            Log.LogMessage(
                MessageImportance.Normal,
                "Configlue JSON Schema output: {0}",
                result.OutputDirectory
            );
        }

        foreach (var diagnostic in result.Diagnostics)
        {
            Log.LogError(
                "Configlue.JsonSchema.MSBuild: {0}: {1}",
                diagnostic.Code,
                diagnostic.Message
            );
        }

        WrittenFiles = [.. result.WrittenFiles.Select(static path => new TaskItem(path))];
        UpToDateFiles = [.. result.UpToDateFiles.Select(static path => new TaskItem(path))];

        foreach (var path in result.WrittenFiles)
        {
            Log.LogMessage(MessageImportance.Normal, "Configlue JSON Schema written: {0}", path);
        }

        return !Log.HasLoggedErrors;
    }
}
