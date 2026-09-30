namespace Configlue.JsonSchema.MSBuild;

/// <summary>Describes a schema file produced by a generation run.</summary>
/// <param name="ModelId">The stable model identifier.</param>
/// <param name="Version">The persisted schema version.</param>
/// <param name="TypeName">The full name of the model type.</param>
/// <param name="FileName">The generated versioned file name.</param>
/// <param name="Content">The generated JSON content.</param>
/// <param name="Written">Whether the file content changed and was written.</param>
public sealed record ConfiglueSchemaDocument(
    string ModelId,
    int Version,
    string TypeName,
    string FileName,
    string Content,
    bool Written
);

/// <summary>Describes a problem encountered while generating schemas.</summary>
/// <param name="Code">A stable diagnostic code.</param>
/// <param name="Message">The human-readable message.</param>
/// <param name="ModelId">The related model identifier, if any.</param>
/// <param name="Version">The related model version, if any.</param>
/// <param name="TypeName">The related model type name, if any.</param>
/// <param name="OutputPath">The related output path, if any.</param>
public sealed record ConfiglueSchemaGenerationDiagnostic(
    string Code,
    string Message,
    string? ModelId = null,
    int? Version = null,
    string? TypeName = null,
    string? OutputPath = null
);

/// <summary>The outcome of a schema-generation run.</summary>
public sealed class ConfiglueSchemaGenerationResult
{
    /// <summary>Gets the resolved output directory, when resolution succeeded.</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>Gets every generated document.</summary>
    public IReadOnlyList<ConfiglueSchemaDocument> Documents { get; init; } = [];

    /// <summary>Gets the full paths of files whose content changed.</summary>
    public IReadOnlyList<string> WrittenFiles { get; init; } = [];

    /// <summary>Gets the full paths of files whose content was already current.</summary>
    public IReadOnlyList<string> UpToDateFiles { get; init; } = [];

    /// <summary>Gets the errors encountered during generation or output.</summary>
    public IReadOnlyList<ConfiglueSchemaGenerationDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Gets whether the run completed without diagnostics.</summary>
    public bool Succeeded => Diagnostics.Count == 0;
}
