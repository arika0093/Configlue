using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using Configlue.Codecs;
using Json.Schema;
using Json.Schema.Generation;
using Json.Schema.Generation.DataAnnotations;

namespace Configlue.JsonSchema.MSBuild;

/// <summary>
/// Discovers <c>[ConfiglueModel]</c> types from a built assembly by reflection and generates the
/// versioned persisted-document JSON Schemas for them.
/// </summary>
public static class ConfiglueSchemaGenerator
{
    private const string ModelAttributeName = "Configlue.ConfiglueModelAttribute";
    private const string SchemasDirectoryName = "schemas";
    private static readonly object DataAnnotationsGate = new();
    private static readonly object SchemaGenerationGate = new();
    private static bool _dataAnnotationsRegistered;

    /// <summary>Runs schema generation for the supplied options.</summary>
    /// <param name="options">The generation inputs.</param>
    /// <returns>The generation result, including diagnostics.</returns>
    public static ConfiglueSchemaGenerationResult Generate(ConfiglueSchemaGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var diagnostics = new List<ConfiglueSchemaGenerationDiagnostic>();

        if (string.IsNullOrWhiteSpace(options.AssemblyPath) || !File.Exists(options.AssemblyPath))
        {
            diagnostics.Add(
                new ConfiglueSchemaGenerationDiagnostic(
                    "CWSC101",
                    $"The built assembly '{options.AssemblyPath}' was not found."
                )
            );
            return new ConfiglueSchemaGenerationResult { Diagnostics = diagnostics };
        }

        var layout = DocumentLayout.Simple;
        if (
            !string.IsNullOrWhiteSpace(options.DocumentLayout)
            && !Enum.TryParse(options.DocumentLayout, ignoreCase: true, out layout)
        )
        {
            diagnostics.Add(
                new ConfiglueSchemaGenerationDiagnostic(
                    "CWSC102",
                    $"The document layout '{options.DocumentLayout}' is not Simple or Detailed."
                )
            );
            return new ConfiglueSchemaGenerationResult { Diagnostics = diagnostics };
        }

        var normalization = NormalizeSchemaBaseUri(options.SchemaBaseUri, diagnostics);
        if (diagnostics.Count > 0)
        {
            return new ConfiglueSchemaGenerationResult { Diagnostics = diagnostics };
        }

        string outputDirectory;
        try
        {
            outputDirectory = ResolveOutputDirectory(options);
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
                new ConfiglueSchemaGenerationDiagnostic(
                    "CWSC103",
                    $"The schema output directory could not be resolved: {exception.Message}",
                    OutputPath: options.OutputPath
                )
            );
            return new ConfiglueSchemaGenerationResult { Diagnostics = diagnostics };
        }

        var loadContext = new ModelLoadContext(options.AssemblyPath);
        Assembly assembly;
        try
        {
            assembly = loadContext.LoadAssembly(Path.GetFullPath(options.AssemblyPath));
        }
        catch (Exception exception)
            when (exception
                    is BadImageFormatException
                        or FileLoadException
                        or FileNotFoundException
                        or IOException
            )
        {
            loadContext.Unload();
            diagnostics.Add(
                new ConfiglueSchemaGenerationDiagnostic(
                    "CWSC104",
                    $"The built assembly '{options.AssemblyPath}' could not be loaded: {exception.Message}"
                )
            );
            return new ConfiglueSchemaGenerationResult { Diagnostics = diagnostics };
        }

        try
        {
            var models = DiscoverModels(assembly, diagnostics);
            if (diagnostics.Count > 0)
            {
                return new ConfiglueSchemaGenerationResult { Diagnostics = diagnostics };
            }

            if (models.Count == 0)
            {
                return new ConfiglueSchemaGenerationResult
                {
                    OutputDirectory = outputDirectory,
                    Diagnostics = diagnostics,
                };
            }

            EnsureLayoutsRegistered();

            var documents = new List<ConfiglueSchemaDocument>();
            var outputNames = new Dictionary<string, ConfiglueModelInfo>(
                StringComparer.OrdinalIgnoreCase
            );
            foreach (var model in models)
            {
                string fileName;
                try
                {
                    fileName = StateSchemaReference.GetFileName(model.Id, model.Version);
                }
                catch (Exception exception)
                    when (exception is ArgumentException or ArgumentOutOfRangeException)
                {
                    diagnostics.Add(
                        new ConfiglueSchemaGenerationDiagnostic(
                            "CWSC105",
                            exception.Message,
                            model.Id,
                            model.Version,
                            model.Type.FullName
                        )
                    );
                    continue;
                }

                if (outputNames.TryGetValue(fileName, out var existing))
                {
                    diagnostics.Add(
                        new ConfiglueSchemaGenerationDiagnostic(
                            "CWSC106",
                            $"Models '{existing.Type.FullName}' and '{model.Type.FullName}' map to the same schema file '{fileName}'.",
                            model.Id,
                            model.Version,
                            model.Type.FullName
                        )
                    );
                    continue;
                }

                outputNames.Add(fileName, model);

                JsonObject schema;
                try
                {
                    schema = GenerateDocument(
                        model,
                        fileName,
                        options.VersionProperty,
                        normalization,
                        layout
                    );
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
                        new ConfiglueSchemaGenerationDiagnostic(
                            "CWSC107",
                            $"JSON Schema generation failed for '{model.Type.FullName}': {exception.Message}",
                            model.Id,
                            model.Version,
                            model.Type.FullName
                        )
                    );
                    continue;
                }

                var content =
                    schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                    + Environment.NewLine;
                documents.Add(
                    new ConfiglueSchemaDocument(
                        model.Id,
                        model.Version,
                        model.Type.FullName ?? model.Type.Name,
                        fileName,
                        content,
                        Written: false
                    )
                );
            }

            if (diagnostics.Count > 0)
            {
                return new ConfiglueSchemaGenerationResult { Diagnostics = diagnostics };
            }

            return WriteDocuments(outputDirectory, documents, diagnostics);
        }
        catch (ReflectionTypeLoadException exception)
        {
            var loaderMessages = exception
                .LoaderExceptions.Where(static e => e is not null)
                .Select(static e => e!.Message)
                .Where(static message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var detail =
                loaderMessages.Length > 0
                    ? $"{exception.Message} ({string.Join("; ", loaderMessages)})"
                    : exception.Message;
            diagnostics.Add(
                new ConfiglueSchemaGenerationDiagnostic(
                    "CWSC108",
                    $"The built assembly type list could not be read: {detail}"
                )
            );
            return new ConfiglueSchemaGenerationResult { Diagnostics = diagnostics };
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static ConfiglueSchemaGenerationResult WriteDocuments(
        string outputDirectory,
        List<ConfiglueSchemaDocument> documents,
        List<ConfiglueSchemaGenerationDiagnostic> diagnostics
    )
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
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
                new ConfiglueSchemaGenerationDiagnostic(
                    "CWSC109",
                    $"The schema output directory '{outputDirectory}' could not be created: {exception.Message}",
                    OutputPath: outputDirectory
                )
            );
            return new ConfiglueSchemaGenerationResult { Diagnostics = diagnostics };
        }

        var writtenFiles = new List<string>();
        var upToDateFiles = new List<string>();
        var writtenDocuments = new List<ConfiglueSchemaDocument>(documents.Count);
        foreach (var document in documents)
        {
            var outputPath = Path.Combine(outputDirectory, document.FileName);
            try
            {
                WriteDocumentAtomically(
                    outputDirectory,
                    outputPath,
                    document.Content,
                    out var written
                );
                if (written)
                {
                    writtenFiles.Add(outputPath);
                    writtenDocuments.Add(document with { Written = true });
                }
                else
                {
                    upToDateFiles.Add(outputPath);
                    writtenDocuments.Add(document with { Written = false });
                }
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
                    new ConfiglueSchemaGenerationDiagnostic(
                        "CWSC110",
                        $"The JSON Schema for '{document.TypeName}' could not be written: {exception.Message}",
                        document.ModelId,
                        document.Version,
                        document.TypeName,
                        outputPath
                    )
                );
            }
        }

        return new ConfiglueSchemaGenerationResult
        {
            OutputDirectory = outputDirectory,
            Documents = writtenDocuments,
            WrittenFiles = writtenFiles,
            UpToDateFiles = upToDateFiles,
            Diagnostics = diagnostics,
        };
    }

    // Writes through a unique temporary file and an atomic move so that concurrent builds sharing an
    // output directory never observe or produce a partially written schema. When another writer has
    // already published identical content the file is reported as up to date instead of rewritten.
    private static void WriteDocumentAtomically(
        string outputDirectory,
        string outputPath,
        string content,
        out bool written
    )
    {
        written = false;
        if (IsCurrent(outputPath, content))
        {
            return;
        }

        var tempPath = Path.Combine(
            outputDirectory,
            $"{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp"
        );
        try
        {
            File.WriteAllText(tempPath, content);
            Exception? lastError = null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    File.Move(tempPath, outputPath, overwrite: true);
                    written = true;
                    return;
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException)
                {
                    lastError = exception;
                    if (IsCurrent(outputPath, content))
                    {
                        return;
                    }

                    Thread.Sleep(15 * (attempt + 1));
                }
            }

            throw lastError!;
        }
        finally
        {
            TryDeleteTemporaryFile(tempPath);
        }
    }

    private static bool IsCurrent(string outputPath, string content)
    {
        if (!File.Exists(outputPath))
        {
            return false;
        }

        try
        {
            // Share delete so a concurrent generator can replace this file while it is being read.
            using var stream = new FileStream(
                outputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            using var reader = new StreamReader(stream);
            return Normalize(reader.ReadToEnd()) == Normalize(content);
        }
        catch (Exception exception)
            when (exception is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteTemporaryFile(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch (Exception exception)
            when (exception is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; a leftover temporary file does not affect the published schema.
            System.Diagnostics.Trace.TraceWarning(
                "Configlue.JsonSchema.MSBuild: could not delete temporary schema file '{0}': {1}",
                tempPath,
                exception.Message
            );
        }
    }

    private static JsonObject GenerateDocument(
        ConfiglueModelInfo model,
        string fileName,
        string versionProperty,
        string? schemaBaseUri,
        DocumentLayout layout
    )
    {
        // Concurrent calls into the pinned schema libraries have raised collection-corruption
        // exceptions. Serialize their generation/serialization boundary within this process;
        // assembly discovery and atomic writes can still run concurrently.
        lock (SchemaGenerationGate)
        {
            return GenerateDocumentCore(model, fileName, versionProperty, schemaBaseUri, layout);
        }
    }

    private static JsonObject GenerateDocumentCore(
        ConfiglueModelInfo model,
        string fileName,
        string versionProperty,
        string? schemaBaseUri,
        DocumentLayout layout
    )
    {
        var configuration = new SchemaGeneratorConfiguration();
        var schema = new JsonSchemaBuilder().FromType(model.Type, configuration).Build();
        var payload =
            JsonSerializer.SerializeToNode(schema) as JsonObject
            ?? throw new InvalidOperationException(
                $"The generated schema for '{model.Type.FullName}' was not a JSON object."
            );

        ApplySecretExtensions(payload, model.Type);

        var schemaId = schemaBaseUri is null
            ? fileName
            : new Uri(new Uri(schemaBaseUri, UriKind.Absolute), fileName).AbsoluteUri;

        return ConfiglueSchemaDocumentLayout.Apply(
            payload,
            model.Id,
            model.Version,
            schemaId,
            includeSchemaProperty: schemaBaseUri is not null,
            layout,
            versionProperty
        );
    }

    private static List<ConfiglueModelInfo> DiscoverModels(
        Assembly assembly,
        List<ConfiglueSchemaGenerationDiagnostic> diagnostics
    )
    {
        var models = new List<ConfiglueModelInfo>();
        foreach (var type in GetLoadableTypes(assembly))
        {
            if (type.IsAbstract || type.IsGenericTypeDefinition || type.IsInterface)
            {
                continue;
            }

            if (!type.IsClass && !(type.IsValueType && !type.IsEnum))
            {
                continue;
            }

            CustomAttributeData? attribute;
            try
            {
                attribute = type.GetCustomAttributesData()
                    .FirstOrDefault(candidate =>
                        candidate.AttributeType.FullName == ModelAttributeName
                    );
            }
            catch (Exception exception)
                when (exception is FileNotFoundException or TypeLoadException)
            {
                diagnostics.Add(
                    new ConfiglueSchemaGenerationDiagnostic(
                        "CWSC111",
                        $"Model metadata for '{type.FullName}' could not be read: {exception.Message}",
                        TypeName: type.FullName
                    )
                );
                continue;
            }

            if (attribute is null)
            {
                continue;
            }

            if (
                attribute.ConstructorArguments.Count == 0
                || attribute.ConstructorArguments[0].Value is not string id
                || string.IsNullOrWhiteSpace(id)
            )
            {
                diagnostics.Add(
                    new ConfiglueSchemaGenerationDiagnostic(
                        "CWSC112",
                        $"Model '{type.FullName}' has no usable schema identifier.",
                        TypeName: type.FullName
                    )
                );
                continue;
            }

            var version = 1;
            foreach (var named in attribute.NamedArguments)
            {
                if (named.MemberName == "Version" && named.TypedValue.Value is int configured)
                {
                    version = configured;
                }
            }

            models.Add(new ConfiglueModelInfo(type, id, version));
        }

        return models
            .OrderBy(model => model.Id, StringComparer.Ordinal)
            .ThenBy(model => model.Version)
            .ToList();
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        // Do not swallow ReflectionTypeLoadException here. A partially loadable type list
        // would silently omit models whose dependencies are missing and report success
        // with partial output. Let Generate surface CWSC108 instead.
        return assembly.GetTypes();
    }

    private static string? NormalizeSchemaBaseUri(
        string? schemaBaseUri,
        List<ConfiglueSchemaGenerationDiagnostic> diagnostics
    )
    {
        if (string.IsNullOrWhiteSpace(schemaBaseUri))
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
                new ConfiglueSchemaGenerationDiagnostic(
                    "CWSC113",
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

        return builder.Uri.AbsoluteUri;
    }

    private static string ResolveOutputDirectory(ConfiglueSchemaGenerationOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.OutputPath))
        {
            return Path.GetFullPath(
                Path.IsPathRooted(options.OutputPath)
                    ? options.OutputPath
                    : Path.Combine(options.ProjectDirectory, options.OutputPath)
            );
        }

        var root = FindSolutionRoot(options);
        return Path.GetFullPath(Path.Combine(root, SchemasDirectoryName));
    }

    private static string FindSolutionRoot(ConfiglueSchemaGenerationOptions options)
    {
        if (
            !string.IsNullOrWhiteSpace(options.SolutionDirectory)
            && Directory.Exists(options.SolutionDirectory)
        )
        {
            return Path.GetFullPath(options.SolutionDirectory);
        }

        var directory = string.IsNullOrWhiteSpace(options.ProjectDirectory)
            ? null
            : new DirectoryInfo(Path.GetFullPath(options.ProjectDirectory));
        while (directory is not null)
        {
            if (directory.EnumerateFiles("*.sln").Any() || directory.EnumerateFiles("*.slnx").Any())
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        if (!string.IsNullOrWhiteSpace(options.ProjectDirectory))
        {
            return Path.GetFullPath(options.ProjectDirectory);
        }

        return Directory.GetCurrentDirectory();
    }

    private static void EnsureLayoutsRegistered()
    {
        if (_dataAnnotationsRegistered)
        {
            return;
        }

        lock (DataAnnotationsGate)
        {
            if (_dataAnnotationsRegistered)
            {
                return;
            }

            DataAnnotationsSupport.AddDataAnnotations();
            _dataAnnotationsRegistered = true;
        }
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private const string SecretValueAttributeName = "Configlue.SecretValueAttribute";
    private const string JsonPropertyNameAttributeName =
        "System.Text.Json.Serialization.JsonPropertyNameAttribute";

    /// <summary>
    /// Marks secret members with the Configlue vendor extension (<c>x-configlue-secret</c>).
    /// Standard <c>writeOnly</c> is never used for this purpose; its semantics differ.
    /// A marked object member implies its whole subtree is sensitive, and a marked
    /// collection member implies its elements are sensitive.
    /// </summary>
    internal static void ApplySecretExtensions(JsonObject payload, Type modelType)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(modelType);
        ApplySecretExtensionsCore(payload, modelType, new HashSet<Type>());
    }

    private static void ApplySecretExtensionsCore(
        JsonObject schema,
        Type modelType,
        HashSet<Type> visited
    )
    {
        if (!visited.Add(modelType))
        {
            return;
        }

        var properties = schema["properties"] as JsonObject;
        if (properties is null)
        {
            return;
        }

        foreach (var member in GetSecretMembers(modelType))
        {
            var key = FindPropertyKey(properties, member.JsonName);
            if (key is null)
            {
                continue;
            }

            if (member.IsSecret && properties[key] is JsonObject memberSchema)
            {
                memberSchema[Configlue.ConfiglueSecrets.JsonSchemaExtensionName] = true;
            }

            var memberType = UnwrapMemberType(member.MemberType);
            if (memberType is null || IsScalarType(memberType))
            {
                continue;
            }

            if (
                properties[key] is JsonObject propertySchema
                && TryResolveTargetSchema(schema, propertySchema, memberType, out var target)
                && target is not null
            )
            {
                ApplySecretExtensionsCore(target, memberType, visited);
            }
            else if (
                TryGetElementType(memberType, out var elementType)
                && elementType is not null
                && properties[key] is JsonObject collectionSchema
                && TryResolveTargetSchema(
                    schema,
                    collectionSchema,
                    elementType,
                    out var elementTarget
                )
                && elementTarget is not null
                && !IsScalarType(elementType)
            )
            {
                ApplySecretExtensionsCore(elementTarget, elementType, visited);
            }
        }
    }

    private sealed record SecretMemberInfo(string JsonName, Type MemberType, bool IsSecret);

    private static List<SecretMemberInfo> GetSecretMembers(Type modelType)
    {
        var result = new List<SecretMemberInfo>();
        foreach (
            var property in modelType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        )
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            result.Add(
                new SecretMemberInfo(
                    GetJsonName(property),
                    property.PropertyType,
                    HasSecretAttribute(property.CustomAttributes)
                )
            );
        }

        foreach (var field in modelType.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            result.Add(
                new SecretMemberInfo(
                    GetJsonName(field),
                    field.FieldType,
                    HasSecretAttribute(field.CustomAttributes)
                )
            );
        }

        return result;
    }

    private static bool HasSecretAttribute(
        IEnumerable<System.Reflection.CustomAttributeData> attributes
    )
    {
        return attributes.Any(attribute =>
            string.Equals(
                attribute.AttributeType.FullName,
                SecretValueAttributeName,
                StringComparison.Ordinal
            )
        );
    }

    private static string GetJsonName(System.Reflection.MemberInfo member)
    {
        foreach (var attribute in member.CustomAttributes)
        {
            if (
                string.Equals(
                    attribute.AttributeType.FullName,
                    JsonPropertyNameAttributeName,
                    StringComparison.Ordinal
                )
                && attribute.ConstructorArguments.Count == 1
                && attribute.ConstructorArguments[0].Value is string name
                && !string.IsNullOrEmpty(name)
            )
            {
                return name;
            }
        }

        return member.Name;
    }

    private static string? FindPropertyKey(JsonObject properties, string jsonName)
    {
        if (properties.ContainsKey(jsonName))
        {
            return jsonName;
        }

        return properties
            .FirstOrDefault(entry =>
                string.Equals(entry.Key, jsonName, StringComparison.OrdinalIgnoreCase)
            )
            .Key;
    }

    private static bool TryResolveTargetSchema(
        JsonObject root,
        JsonObject propertySchema,
        Type memberType,
        out JsonObject? target
    )
    {
        target = null;
        if (
            propertySchema["$ref"] is JsonValue reference
            && reference.TryGetValue<string>(out var pointer)
            && !string.IsNullOrEmpty(pointer)
        )
        {
            target = ResolvePointer(root, pointer);
            return target is not null;
        }

        if (propertySchema["properties"] is JsonObject)
        {
            target = propertySchema;
            return true;
        }

        if (propertySchema["items"] is JsonObject items)
        {
            if (
                items["$ref"] is JsonValue itemReference
                && itemReference.TryGetValue<string>(out var itemPointer)
                && !string.IsNullOrEmpty(itemPointer)
            )
            {
                target = ResolvePointer(root, itemPointer);
                return target is not null;
            }

            if (items["properties"] is JsonObject)
            {
                target = items;
                return true;
            }
        }

        if (propertySchema["$defs"] is JsonObject || propertySchema["definitions"] is JsonObject)
        {
            target = propertySchema;
            return true;
        }

        // Unwrap the member type: collections resolve through items, objects inline.
        if (TryGetElementType(memberType, out var elementType) && elementType is not null)
        {
            return TryResolveTargetSchema(root, propertySchema, elementType, out target);
        }

        return false;
    }

    private static JsonObject? ResolvePointer(JsonObject root, string pointer)
    {
        // Local pointers such as "#/$defs/NestedFixture".
        if (!pointer.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }

        JsonNode? current = root;
        foreach (var segment in pointer[2..].Split('/'))
        {
            var name = segment
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            current = (current as JsonObject)?[name];
            if (current is null)
            {
                return null;
            }
        }

        return current as JsonObject;
    }

    private static Type? UnwrapMemberType(Type type)
    {
        var current = Nullable.GetUnderlyingType(type) ?? type;
        if (TryGetElementType(current, out var elementType))
        {
            return elementType;
        }

        return current;
    }

    private static bool TryGetElementType(Type type, out Type? elementType)
    {
        elementType = null;
        if (type.IsArray)
        {
            elementType = type.GetElementType();
            return elementType is not null;
        }

        if (!type.IsGenericType)
        {
            return false;
        }

        foreach (var candidate in type.GetInterfaces().Prepend(type))
        {
            if (
                candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            )
            {
                elementType = candidate.GetGenericArguments()[0];
                return true;
            }
        }

        return false;
    }

    private static bool IsScalarType(Type type)
    {
        var current = Nullable.GetUnderlyingType(type) ?? type;
        return current.IsPrimitive
            || current.IsEnum
            || current == typeof(string)
            || current == typeof(decimal)
            || current == typeof(DateTime)
            || current == typeof(DateTimeOffset)
            || current == typeof(Guid)
            || current == typeof(Uri);
    }

    private sealed record ConfiglueModelInfo(Type Type, string Id, int Version);

    private sealed class ModelLoadContext : AssemblyLoadContext
    {
        private readonly string _directory;
        private readonly AssemblyDependencyResolver? _resolver;

        public ModelLoadContext(string assemblyPath)
            : base($"ConfiglueSchemaModels-{Guid.NewGuid():N}", isCollectible: true)
        {
            var fullPath = Path.GetFullPath(assemblyPath);
            _directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
            var depsPath = Path.ChangeExtension(fullPath, ".deps.json");
            _resolver = File.Exists(depsPath) ? new AssemblyDependencyResolver(fullPath) : null;
        }

        // Assemblies are loaded from memory so the build outputs are never locked by long-lived
        // MSBuild worker nodes, which would break subsequent incremental builds.
        public Assembly LoadAssembly(string path) =>
            LoadFromPath(path)
            ?? throw new FileLoadException($"The assembly '{path}' could not be loaded.");

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var resolved = _resolver?.ResolveAssemblyToPath(assemblyName);
            if (resolved is not null)
            {
                return LoadFromPath(resolved);
            }

            if (assemblyName.Name is { } name)
            {
                var candidate = Path.Combine(_directory, name + ".dll");
                if (File.Exists(candidate))
                {
                    return LoadFromPath(candidate);
                }
            }

            return null;
        }

        private Assembly? LoadFromPath(string path)
        {
            try
            {
                return LoadFromStream(new MemoryStream(File.ReadAllBytes(path)));
            }
            catch (BadImageFormatException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }
}
