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

        Assembly assembly;
        ModelLoadContext loadContext;
        try
        {
            loadContext = new ModelLoadContext(options.AssemblyPath);
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
            diagnostics.Add(
                new ConfiglueSchemaGenerationDiagnostic(
                    "CWSC108",
                    $"The built assembly type list could not be read: {exception.Message}"
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
        var configuration = new SchemaGeneratorConfiguration();
        var schema = new JsonSchemaBuilder().FromType(model.Type, configuration).Build();
        var payload =
            JsonSerializer.SerializeToNode(schema) as JsonObject
            ?? throw new InvalidOperationException(
                $"The generated schema for '{model.Type.FullName}' was not a JSON object."
            );

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
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(static type => type is not null)!;
        }
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
