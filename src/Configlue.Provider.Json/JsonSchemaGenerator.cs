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
public sealed record JsonSchemaDocument(
    ConfiglueModelSchema Model,
    string FileName,
    JsonNode Schema
);

/// <summary>Describes a problem encountered while generating or writing JSON Schemas.</summary>
public sealed record JsonSchemaGenerationDiagnostic(
    string Code,
    string Message,
    Type? ModelType = null,
    string? ModelId = null,
    int? Version = null,
    string? OutputPath = null
);

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
public static class JsonSchemaGenerator
{
    /// <summary>
    /// Generates JSON Schema documents in memory for the supplied versioned options models.
    /// </summary>
    /// <param name="models">The generated model schemas to export.</param>
    /// <param name="resolver">The JSON type-info resolver, preferably source-generated for trimming and NativeAOT.</param>
    /// <param name="schemaBaseUri">An optional absolute base URI for generated schema identifiers. The schema file name is appended to it, and the exported configuration schema includes an optional <c>$schema</c> property when this value is set. The URI must not include a query or fragment.</param>
    public static JsonSchemaGenerationResult Generate(
        IEnumerable<ConfiglueModelSchema> models,
        IJsonTypeInfoResolver resolver,
        string? schemaBaseUri = null
    )
    {
        if (models is null)
            throw new ArgumentNullException(nameof(models));
        if (resolver is null)
            throw new ArgumentNullException(nameof(resolver));

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
            return CreateResult([], [], diagnostics);

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
                    schemaBaseUri is not null
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
        string? schemaBaseUri = null
    )
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment> =>
        Generate([TModel.ConfiglueSchema], resolver, schemaBaseUri);

    /// <summary>
    /// Generates and writes JSON Schema documents for the supplied versioned options models.
    /// </summary>
    /// <param name="models">The generated model schemas to export.</param>
    /// <param name="outputDirectory">The directory in which schema files are written.</param>
    /// <param name="resolver">The JSON type-info resolver, preferably source-generated for trimming and NativeAOT.</param>
    /// <param name="schemaBaseUri">An optional absolute base URI for generated schema identifiers. The schema file name is appended to it, and the exported configuration schema includes an optional <c>$schema</c> property when this value is set. The URI must not include a query or fragment.</param>
    public static JsonSchemaGenerationResult Write(
        IEnumerable<ConfiglueModelSchema> models,
        string outputDirectory,
        IJsonTypeInfoResolver resolver,
        string? schemaBaseUri = null
    )
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException(
                "An output directory is required.",
                nameof(outputDirectory)
            );
        var generation = Generate(models, resolver, schemaBaseUri);
        if (!generation.Succeeded)
            return generation;

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
        string? schemaBaseUri = null
    )
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment> =>
        Write([TModel.ConfiglueSchema], outputDirectory, resolver, schemaBaseUri);

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
                continue;

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

#if NET9_0_OR_GREATER
    private sealed class JsonSchemaNodeTransformer
    {
        private readonly IJsonTypeInfoResolver _resolver;
        private readonly JsonSerializerOptions _jsonOptions;
        private int _nextEmbeddedSchemaId;

        public JsonSchemaNodeTransformer(
            IJsonTypeInfoResolver resolver,
            JsonSerializerOptions jsonOptions
        )
        {
            _resolver = resolver;
            _jsonOptions = jsonOptions;
            Options = new JsonSchemaExporterOptions { TransformSchemaNode = TransformSchemaNode };
        }

        public JsonSchemaExporterOptions Options { get; }

        private JsonNode TransformSchemaNode(JsonSchemaExporterContext context, JsonNode node)
        {
            var schemaOverride = GetAttributes<JsonSchemaOverrideAttribute>(
                    context.PropertyInfo?.AttributeProvider
                )
                .FirstOrDefault();
            var oneOf = GetAttributes<JsonSchemaOneOfAttribute>(
                    context.PropertyInfo?.AttributeProvider
                )
                .FirstOrDefault();
            if (schemaOverride is not null && oneOf is not null)
                throw new JsonException(
                    "A property cannot declare both a JSON Schema override and a oneOf schema."
                );

            var isBooleanSchema =
                node is JsonValue converterSchema && converterSchema.TryGetValue<bool>(out _);
            if ((schemaOverride is not null || oneOf is not null) && !isBooleanSchema)
                throw new JsonException(
                    "JSON Schema override attributes can only be used with properties that export a boolean schema."
                );

            if (isBooleanSchema)
            {
                if (schemaOverride is not null)
                {
                    var replacement = JsonNode.Parse(schemaOverride.Schema);
                    if (
                        replacement is not JsonObject
                        && (
                            replacement is not JsonValue replacementValue
                            || !replacementValue.TryGetValue<bool>(out _)
                        )
                    )
                        throw new JsonException(
                            "A JSON Schema override must be a JSON object or boolean schema."
                        );
                    node = replacement;
                }
                else if (oneOf is not null)
                {
                    if (
                        oneOf.Types.Length == 0
                        || oneOf.Types.Any(type => type is null)
                        || oneOf.Types.Distinct().Count() != oneOf.Types.Length
                    )
                        throw new JsonException(
                            "A oneOf schema must specify distinct, non-null types."
                        );

                    var alternatives = new JsonArray();
                    foreach (var alternativeType in oneOf.Types)
                    {
                        var alternativeTypeInfo = _resolver.GetTypeInfo(
                            alternativeType,
                            _jsonOptions
                        );
                        var alternativeSchema = alternativeTypeInfo is null
                            ? CreatePrimitiveSchema(alternativeType)
                            : JsonSchemaExporter.GetJsonSchemaAsNode(alternativeTypeInfo, Options);
                        if (alternativeSchema is null)
                            throw new JsonException(
                                $"The configured JSON type-info resolver does not provide metadata for oneOf type '{alternativeType.FullName}', and the type is not a supported primitive."
                            );

                        if (
                            alternativeSchema is JsonObject alternativeObject
                            && alternativeObject["$defs"] is JsonObject
                        )
                            alternativeObject["$id"] ??=
                                $"urn:configlue:oneof:{_nextEmbeddedSchemaId++}";
                        alternatives.Add(alternativeSchema);
                    }
                    node = new JsonObject { ["oneOf"] = alternatives };
                }
            }

            if (node is not JsonObject schema)
            {
                if (node is not JsonValue value || !value.TryGetValue<bool>(out var booleanSchema))
                    return node;

                schema = booleanSchema
                    ? new JsonObject()
                    : new JsonObject { ["not"] = new JsonObject() };
            }

            var description = GetAttributes<DescriptionAttribute>(
                    context.PropertyInfo?.AttributeProvider
                )
                .FirstOrDefault();
            if (description is not null)
                schema["description"] = description.Description;
            else
            {
                var displayDescription = GetAttributes<DisplayAttribute>(
                        context.PropertyInfo?.AttributeProvider
                    )
                    .FirstOrDefault()
                    ?.Description;
                if (displayDescription is not null)
                    schema["description"] = displayDescription;
            }

            var displayName = GetAttributes<DisplayAttribute>(
                    context.PropertyInfo?.AttributeProvider
                )
                .FirstOrDefault()
                ?.Name;
            if (displayName is not null)
                schema["title"] = displayName;

            JsonSchemaValidationAttributeMapper.AddRequiredProperties(schema, context.TypeInfo);
            foreach (
                var attribute in GetAttributes<ValidationAttribute>(
                    context.PropertyInfo?.AttributeProvider
                )
            )
                JsonSchemaValidationAttributeMapper.Apply(schema, attribute, context.TypeInfo);
            return schema;
        }

        private static JsonNode? CreatePrimitiveSchema(Type type)
        {
            var nullableType = Nullable.GetUnderlyingType(type);
            if (nullableType is not null)
                type = nullableType;

            if (type.IsEnum)
                return null;

            var schemaType = Type.GetTypeCode(type) switch
            {
                TypeCode.String or TypeCode.Char => "string",
                TypeCode.Boolean => "boolean",
                TypeCode.Byte
                or TypeCode.SByte
                or TypeCode.Int16
                or TypeCode.UInt16
                or TypeCode.Int32
                or TypeCode.UInt32
                or TypeCode.Int64
                or TypeCode.UInt64 => "integer",
                TypeCode.Single or TypeCode.Double or TypeCode.Decimal => "number",
                _ => null,
            };
            if (schemaType is null)
                return null;

            var schema = new JsonObject { ["type"] = schemaType };
            if (nullableType is not null)
                schema["type"] = new JsonArray(schemaType, "null");
            return schema;
        }
    }

    private static IEnumerable<TAttribute> GetAttributes<TAttribute>(
        ICustomAttributeProvider? attributeProvider
    )
        where TAttribute : Attribute =>
        attributeProvider
            ?.GetCustomAttributes(typeof(TAttribute), inherit: true)
            .OfType<TAttribute>()
        ?? [];

    private static JsonNode CreatePersistedDocumentSchema(
        JsonNode exportedModelSchema,
        ConfiglueModelSchema model,
        string schemaId,
        bool includeConfigurationSchema
    )
    {
        if (exportedModelSchema is not JsonObject payload)
        {
            throw new InvalidOperationException("The exported JSON schema root must be an object.");
        }

        var definitions = payload["$defs"]?.DeepClone();
        if (definitions is not null)
        {
            RemoveRequiredProperties(definitions);
        }

        payload.Remove("$schema");
        payload.Remove("$id");
        payload.Remove("$defs");
        RemoveRequiredProperties(payload);

        var properties = new JsonObject
        {
            ["$configlue"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["const"] = model.Id },
                    ["version"] = new JsonObject { ["const"] = model.Version },
                },
                ["required"] = new JsonArray("id", "version"),
                ["additionalProperties"] = false,
            },
            ["$value"] = payload,
        };
        if (includeConfigurationSchema)
        {
            properties["$schema"] = new JsonObject { ["type"] = "string" };
        }

        var schema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = schemaId,
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray("$configlue", "$value"),
            ["additionalProperties"] = false,
        };
        if (definitions is not null)
        {
            schema["$defs"] = definitions;
        }

        return schema;
    }

    private static void RemoveRequiredProperties(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            jsonObject.Remove("required");
            foreach (var child in jsonObject.Where(static item => item.Value is not null))
            {
                RemoveRequiredProperties(child.Value!);
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var child in jsonArray.Where(static item => item is not null))
            {
                RemoveRequiredProperties(child!);
            }
        }
    }
#endif
}
