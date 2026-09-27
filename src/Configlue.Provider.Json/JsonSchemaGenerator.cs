using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
#if NET9_0_OR_GREATER
using System.Text.Json.Schema;
#endif

namespace Configlue.Provider.Json;

/// <summary>Represents a generated JSON Schema document.</summary>
public sealed record JsonSchemaDocument
{
    /// <summary>Gets or initializes the <see cref="Model"/> value.</summary>
    public ConfiglueModelSchema Model { get; init; }

    /// <summary>Gets or initializes the <see cref="FileName"/> value.</summary>
    public string FileName { get; init; }

    /// <summary>Gets or initializes the <see cref="Schema"/> value.</summary>
    public JsonNode Schema { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="Model">The initial value for the <see cref="Model"/> property.</param>
    /// <param name="FileName">The initial value for the <see cref="FileName"/> property.</param>
    /// <param name="Schema">The initial value for the <see cref="Schema"/> property.</param>
    public JsonSchemaDocument(ConfiglueModelSchema Model, string FileName, JsonNode Schema)
    {
        this.Model = Model;
        this.FileName = FileName;
        this.Schema = Schema;
    }

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="Model">Receives the current <see cref="Model"/> value.</param>
    /// <param name="FileName">Receives the current <see cref="FileName"/> value.</param>
    /// <param name="Schema">Receives the current <see cref="Schema"/> value.</param>
    public void Deconstruct(
        out ConfiglueModelSchema Model,
        out string FileName,
        out JsonNode Schema
    )
    {
        Model = this.Model;
        FileName = this.FileName;
        Schema = this.Schema;
    }
}

/// <summary>Describes a problem encountered while generating or writing JSON Schemas.</summary>
public sealed record JsonSchemaGenerationDiagnostic
{
    /// <summary>Gets or initializes the <see cref="Code"/> value.</summary>
    public string Code { get; init; }

    /// <summary>Gets or initializes the <see cref="Message"/> value.</summary>
    public string Message { get; init; }

    /// <summary>Gets or initializes the <see cref="ModelType"/> value.</summary>
    public Type? ModelType { get; init; }

    /// <summary>Gets or initializes the <see cref="ModelId"/> value.</summary>
    public string? ModelId { get; init; }

    /// <summary>Gets or initializes the <see cref="Version"/> value.</summary>
    public int? Version { get; init; }

    /// <summary>Gets or initializes the <see cref="OutputPath"/> value.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="Code">The initial value for the <see cref="Code"/> property.</param>
    /// <param name="Message">The initial value for the <see cref="Message"/> property.</param>
    /// <param name="ModelType">The initial value for the <see cref="ModelType"/> property.</param>
    /// <param name="ModelId">The initial value for the <see cref="ModelId"/> property.</param>
    /// <param name="Version">The initial value for the <see cref="Version"/> property.</param>
    /// <param name="OutputPath">The initial value for the <see cref="OutputPath"/> property.</param>
    public JsonSchemaGenerationDiagnostic(
        string Code,
        string Message,
        Type? ModelType = null,
        string? ModelId = null,
        int? Version = null,
        string? OutputPath = null
    )
    {
        this.Code = Code;
        this.Message = Message;
        this.ModelType = ModelType;
        this.ModelId = ModelId;
        this.Version = Version;
        this.OutputPath = OutputPath;
    }

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="Code">Receives the current <see cref="Code"/> value.</param>
    /// <param name="Message">Receives the current <see cref="Message"/> value.</param>
    /// <param name="ModelType">Receives the current <see cref="ModelType"/> value.</param>
    /// <param name="ModelId">Receives the current <see cref="ModelId"/> value.</param>
    /// <param name="Version">Receives the current <see cref="Version"/> value.</param>
    /// <param name="OutputPath">Receives the current <see cref="OutputPath"/> value.</param>
    public void Deconstruct(
        out string Code,
        out string Message,
        out Type? ModelType,
        out string? ModelId,
        out int? Version,
        out string? OutputPath
    )
    {
        Code = this.Code;
        Message = this.Message;
        ModelType = this.ModelType;
        ModelId = this.ModelId;
        Version = this.Version;
        OutputPath = this.OutputPath;
    }
}

/// <summary>Contains generated documents, written paths, and diagnostics.</summary>
public sealed class JsonSchemaGenerationResult
{
    internal JsonSchemaGenerationResult(
        IReadOnlyList<JsonSchemaDocument> documents,
        IReadOnlyList<string> writtenFiles,
        IReadOnlyList<JsonSchemaGenerationDiagnostic> diagnostics
    )
    {
        Documents = documents;
        WrittenFiles = writtenFiles;
        Diagnostics = diagnostics;
    }

    /// <summary>Gets the generated documents. Empty if schema generation failed.</summary>
    public IReadOnlyList<JsonSchemaDocument> Documents { get; }

    /// <summary>Gets the full paths successfully written by <see cref="JsonSchemaGenerator.Write"/>.</summary>
    public IReadOnlyList<string> WrittenFiles { get; }

    /// <summary>Gets the errors encountered during generation or output.</summary>
    public IReadOnlyList<JsonSchemaGenerationDiagnostic> Diagnostics { get; }

    /// <summary>Gets whether generation or output completed without diagnostics.</summary>
    public bool Succeeded => Diagnostics.Count == 0;
}

/// <summary>Generates and writes JSON Schemas from generated Configlue model contracts.</summary>
public static partial class JsonSchemaGenerator
{
    /// <summary>
    /// Generates JSON Schema documents in memory for the supplied versioned options models.
    /// </summary>
    /// <param name="models">The generated model schemas to export.</param>
    /// <param name="resolver">The JSON type-info resolver, preferably source-generated for trimming and NativeAOT.</param>
    /// <param name="schemaBaseUri">An optional absolute base URI for generated schema identifiers. The schema file name is appended to it, and the exported configuration schema includes an optional <c>$schema</c> property when this value is set. The URI must not include a query or fragment.</param>
    /// <param name="documentLayout">The persisted document structure the schema validates. Reads accept both layouts; the default simple layout stores the version inline.</param>
    public static JsonSchemaGenerationResult Generate(
        IEnumerable<ConfiglueModelSchema> models,
        IJsonTypeInfoResolver resolver,
        string? schemaBaseUri = null,
        DocumentLayoutOptions? documentLayout = null
    )
    {
        if (models is null)
        {
            throw new ArgumentNullException(nameof(models));
        }
        if (resolver is null)
        {
            throw new ArgumentNullException(nameof(resolver));
        }

        var diagnostics = new List<JsonSchemaGenerationDiagnostic>();
        var normalizedSchemaBaseUri = NormalizeSchemaBaseUri(schemaBaseUri, diagnostics);
        if (diagnostics.Count > 0)
        {
            return CreateResult([], [], diagnostics);
        }

#if NET9_0_OR_GREATER
        var candidates = ValidateModels(models, diagnostics);
#else
        _ = ValidateModels(models, diagnostics);
#endif
        if (diagnostics.Count > 0)
        {
            return CreateResult([], [], diagnostics);
        }

#if !NET9_0_OR_GREATER
        diagnostics.Add(
            new JsonSchemaGenerationDiagnostic(
                "CWSC001",
                "JSON Schema export requires a target framework with System.Text.Json schema export support."
            )
        );
        return CreateResult([], [], diagnostics);
#else
        var documents = new List<JsonSchemaDocument>(candidates.Count);
        var jsonOptions = resolver is JsonSerializerContext context
            ? context.Options
            : new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = resolver };
        foreach (var model in candidates)
        {
            try
            {
                var typeInfo = resolver.GetTypeInfo(model.ModelType, jsonOptions);
                if (typeInfo is null)
                {
                    diagnostics.Add(
                        CreateModelDiagnostic(
                            "CWSC002",
                            $"The configured JSON type-info resolver does not provide metadata for '{model.ModelType.FullName}'.",
                            model
                        )
                    );
                    continue;
                }

                var nodeTransformer = new JsonSchemaNodeTransformer(resolver, jsonOptions);
                var schema = JsonSchemaExporter.GetJsonSchemaAsNode(
                    typeInfo,
                    nodeTransformer.Options
                );
                if (schema is null)
                {
                    diagnostics.Add(
                        CreateModelDiagnostic(
                            "CWSC003",
                            $"The JSON schema exporter returned no schema for '{model.ModelType.FullName}'.",
                            model
                        )
                    );
                    continue;
                }

                var fileName = JsonSchemaGeneration.GetSchemaFileName(model.Id, model.Version);
                var schemaId = normalizedSchemaBaseUri is null
                    ? fileName
                    : new Uri(normalizedSchemaBaseUri, fileName).AbsoluteUri;
                schema = CreatePersistedDocumentSchema(
                    schema,
                    model,
                    schemaId,
                    schemaBaseUri is not null,
                    documentLayout
                );
                documents.Add(new JsonSchemaDocument(model, fileName, schema));
            }
            catch (Exception exception)
                when (exception
                        is ArgumentException
                            or InvalidOperationException
                            or JsonException
                            or NotSupportedException
                )
            {
                diagnostics.Add(
                    CreateModelDiagnostic(
                        "CWSC004",
                        $"JSON Schema export failed for '{model.ModelType.FullName}': {exception.Message}",
                        model
                    )
                );
            }
        }

        return diagnostics.Count == 0
            ? CreateResult(documents, [], diagnostics)
            : CreateResult([], [], diagnostics);
#endif
    }

    /// <summary>Generates a schema for one source-generated Configlue model.</summary>
    public static JsonSchemaGenerationResult Generate<TModel, TFragment>(
        IJsonTypeInfoResolver resolver,
        string? schemaBaseUri = null,
        DocumentLayoutOptions? documentLayout = null
    )
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment> =>
        Generate([TModel.ConfiglueSchema], resolver, schemaBaseUri, documentLayout);

    /// <summary>
    /// Generates and writes JSON Schema documents for the supplied versioned options models.
    /// </summary>
    /// <param name="models">The generated model schemas to export.</param>
    /// <param name="outputDirectory">The directory in which schema files are written.</param>
    /// <param name="resolver">The JSON type-info resolver, preferably source-generated for trimming and NativeAOT.</param>
    /// <param name="schemaBaseUri">An optional absolute base URI for generated schema identifiers. The schema file name is appended to it, and the exported configuration schema includes an optional <c>$schema</c> property when this value is set. The URI must not include a query or fragment.</param>
    /// <param name="documentLayout">The persisted document structure the schema validates. Reads accept both layouts; the default simple layout stores the version inline.</param>
    public static JsonSchemaGenerationResult Write(
        IEnumerable<ConfiglueModelSchema> models,
        string outputDirectory,
        IJsonTypeInfoResolver resolver,
        string? schemaBaseUri = null,
        DocumentLayoutOptions? documentLayout = null
    )
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException(
                "An output directory is required.",
                nameof(outputDirectory)
            );
        }
        var generation = Generate(models, resolver, schemaBaseUri, documentLayout);
        if (!generation.Succeeded)
        {
            return generation;
        }

        string fullOutputDirectory;
        try
        {
            fullOutputDirectory = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(fullOutputDirectory);
        }
        catch (Exception exception)
            when (exception
                    is ArgumentException
                        or IOException
                        or NotSupportedException
                        or UnauthorizedAccessException
            )
        {
            return CreateResult(
                [],
                [],
                [
                    new JsonSchemaGenerationDiagnostic(
                        "CWSC005",
                        $"The schema output directory '{outputDirectory}' could not be created: {exception.Message}",
                        OutputPath: outputDirectory
                    ),
                ]
            );
        }

        var writtenFiles = new List<string>();
        var diagnostics = new List<JsonSchemaGenerationDiagnostic>();
        foreach (var document in generation.Documents)
        {
            var outputPath = Path.Combine(fullOutputDirectory, document.FileName);
            try
            {
                File.WriteAllText(
                    outputPath,
                    document.Schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                        + Environment.NewLine
                );
                writtenFiles.Add(outputPath);
            }
            catch (Exception exception)
                when (exception
                        is ArgumentException
                            or IOException
                            or NotSupportedException
                            or UnauthorizedAccessException
                )
            {
                diagnostics.Add(
                    CreateModelDiagnostic(
                        "CWSC006",
                        $"The JSON Schema for '{document.Model.ModelType.FullName}' could not be written: {exception.Message}",
                        document.Model,
                        outputPath
                    )
                );
            }
        }

        return CreateResult(generation.Documents, writtenFiles, diagnostics);
    }

    /// <summary>Generates and writes a schema for one source-generated Configlue model.</summary>
    public static JsonSchemaGenerationResult Write<TModel, TFragment>(
        string outputDirectory,
        IJsonTypeInfoResolver resolver,
        string? schemaBaseUri = null,
        DocumentLayoutOptions? documentLayout = null
    )
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment> =>
        Write([TModel.ConfiglueSchema], outputDirectory, resolver, schemaBaseUri, documentLayout);

    /// <summary>
    /// Writes schemas when <c>--cw-generate-json-schema &lt;directory&gt;</c> is present in the
    /// application arguments. The host process remains in control of its lifetime and exit code.
    /// </summary>
    /// <param name="arguments">The arguments passed to the application entry point.</param>
    /// <param name="models">The registered generated model schemas.</param>
    /// <param name="resolver">The JSON type-info resolver, preferably source-generated for trimming and NativeAOT.</param>
    /// <param name="result">The write result when the option was present; otherwise <see langword="null"/>.</param>
    /// <param name="schemaBaseUri">An optional absolute base URI for generated schema identifiers.</param>
    /// <returns><see langword="true"/> when the command-line option was handled.</returns>
    public static bool TryWriteFromCommandLine(
        IReadOnlyList<string> arguments,
        IEnumerable<ConfiglueModelSchema> models,
        IJsonTypeInfoResolver resolver,
        out JsonSchemaGenerationResult? result,
        string? schemaBaseUri = null
    )
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var outputDirectory = default(string);
        var optionCount = 0;
        var index = 0;
        while (index < arguments.Count)
        {
            var argument = arguments[index++];
            if (string.Equals(argument, "--cw-generate-json-schema", StringComparison.Ordinal))
            {
                optionCount++;
                if (
                    index < arguments.Count
                    && !arguments[index].StartsWith("--", StringComparison.Ordinal)
                )
                {
                    outputDirectory = arguments[index++];
                }
                else
                {
                    outputDirectory = null;
                }

                continue;
            }

            const string optionPrefix = "--cw-generate-json-schema=";
            if (argument.StartsWith(optionPrefix, StringComparison.Ordinal))
            {
                optionCount++;
                outputDirectory = argument[optionPrefix.Length..];
            }
        }

        if (optionCount == 0)
        {
            result = null;
            return false;
        }

        if (optionCount > 1)
        {
            result = CreateResult(
                [],
                [],
                [
                    new JsonSchemaGenerationDiagnostic(
                        "CWSC013",
                        "Specify --cw-generate-json-schema only once."
                    ),
                ]
            );
            return true;
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            result = CreateResult(
                [],
                [],
                [
                    new JsonSchemaGenerationDiagnostic(
                        "CWSC013",
                        "--cw-generate-json-schema requires an output directory."
                    ),
                ]
            );
            return true;
        }

        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(resolver);
        result = Write(models, outputDirectory, resolver, schemaBaseUri);
        return true;
    }

    private static List<ConfiglueModelSchema> ValidateModels(
        IEnumerable<ConfiglueModelSchema> models,
        List<JsonSchemaGenerationDiagnostic> diagnostics
    )
    {
        var candidates = new List<ConfiglueModelSchema>();
        var seenModels = new HashSet<(Type ModelType, string ModelId, int Version)>();
        var outputNames = new Dictionary<string, ConfiglueModelSchema>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var model in models)
        {
            if (model is null)
            {
                diagnostics.Add(
                    new JsonSchemaGenerationDiagnostic(
                        "CWSC007",
                        "A null options model metadata entry was supplied."
                    )
                );
                continue;
            }

            if (model.ModelType is null)
            {
                diagnostics.Add(
                    CreateModelDiagnostic("CWSC008", "The options model type is null.", model)
                );
                continue;
            }

            if (!JsonSchemaGeneration.IsValidModelId(model.Id))
            {
                diagnostics.Add(
                    CreateModelDiagnostic(
                        "CWSC009",
                        $"Options model ID '{model.Id}' cannot be used as a schema file name.",
                        model
                    )
                );
                continue;
            }

            if (model.Version <= 0)
            {
                diagnostics.Add(
                    CreateModelDiagnostic(
                        "CWSC010",
                        $"Options model version must be positive; received {model.Version}.",
                        model
                    )
                );
                continue;
            }

            if (!seenModels.Add((model.ModelType, model.Id, model.Version)))
            {
                continue;
            }

            var fileName = JsonSchemaGeneration.GetSchemaFileName(model.Id, model.Version);
            if (outputNames.TryGetValue(fileName, out var existing))
            {
                diagnostics.Add(
                    CreateModelDiagnostic(
                        "CWSC011",
                        $"Models '{existing.ModelType.FullName}' and '{model.ModelType.FullName}' map to the same schema file '{fileName}'.",
                        existing
                    )
                );
                diagnostics.Add(
                    CreateModelDiagnostic(
                        "CWSC011",
                        $"Models '{existing.ModelType.FullName}' and '{model.ModelType.FullName}' map to the same schema file '{fileName}'.",
                        model
                    )
                );
                continue;
            }

            outputNames.Add(fileName, model);
            candidates.Add(model);
        }

        return candidates
            .OrderBy(model => model.Id, StringComparer.Ordinal)
            .ThenBy(model => model.Version)
            .ToList();
    }

    private static JsonSchemaGenerationDiagnostic CreateModelDiagnostic(
        string code,
        string message,
        ConfiglueModelSchema model,
        string? outputPath = null
    ) => new(code, message, model.ModelType, model.Id, model.Version, outputPath);

    private static Uri? NormalizeSchemaBaseUri(
        string? schemaBaseUri,
        List<JsonSchemaGenerationDiagnostic> diagnostics
    )
    {
        if (schemaBaseUri is null)
        {
            return null;
        }

        if (
            !Uri.TryCreate(schemaBaseUri, UriKind.Absolute, out var baseUri)
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment)
        )
        {
            diagnostics.Add(
                new JsonSchemaGenerationDiagnostic(
                    "CWSC012",
                    "The schema base URI must be an absolute URI without a query or fragment."
                )
            );
            return null;
        }

        var builder = new UriBuilder(baseUri);
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    private static JsonSchemaGenerationResult CreateResult(
        IReadOnlyList<JsonSchemaDocument> documents,
        IReadOnlyList<string> writtenFiles,
        IReadOnlyList<JsonSchemaGenerationDiagnostic> diagnostics
    ) =>
        new(
            Array.AsReadOnly(documents.ToArray()),
            Array.AsReadOnly(writtenFiles.ToArray()),
            Array.AsReadOnly(diagnostics.ToArray())
        );
}
