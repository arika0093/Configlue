using System.Text.Json;
using Configlue;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Source.Environment;
using SharpYaml;

namespace Configlue.Source.Common;

/// <summary>The file format used by a common file layer.</summary>
public enum CommonSourceFileFormat
{
    /// <summary>Infer the format from the file extension (<c>.yaml</c>/<c>.yml</c>, <c>.xml</c>, otherwise JSON).</summary>
    Auto,

    /// <summary>JavaScript Object Notation.</summary>
    Json,

    /// <summary>YAML Ain't Markup Language.</summary>
    Yaml,

    /// <summary>Extensible Markup Language.</summary>
    Xml,
}

/// <summary>An explicitly selected normal write destination for common sources.</summary>
public enum CommonSourceWriteLayer
{
    /// <summary>Write to the global per-user file.</summary>
    Global,

    /// <summary>Write to the local file.</summary>
    Local,

    /// <summary>Write to the explicitly selected file.</summary>
    Specific,
}

/// <summary>Options for the common global/local/explicit/environment source layout.</summary>
public sealed class CommonSourceOptions
{
    /// <summary>The application ID used to select the standard per-user directory.</summary>
    public required string ApplicationId { get; init; }

    /// <summary>The file name used in the global directory and, by default, the local directory.</summary>
    public required string GlobalFileName { get; init; }

    /// <summary>Optional local file path. Defaults to the current directory plus GlobalFileName.</summary>
    public string? LocalFilePath { get; init; }

    /// <summary>Optional explicitly selected file path, commonly supplied by application arguments.</summary>
    public string? SpecificFilePath { get; init; }

    /// <summary>Environment prefix; a null value omits the environment layer.</summary>
    public string? EnvironmentPrefix { get; init; }

    /// <summary>Optional write destination override. By default, a supplied specific file is used; otherwise the local file is used.</summary>
    public CommonSourceWriteLayer? WriteLayer { get; init; }

    /// <summary>JSON serialization and property naming options shared by the file layers.</summary>
    public JsonSerializerOptions? SerializerOptions { get; init; }

    /// <summary>The default file format for layers without an explicit format. Auto infers from each path.</summary>
    public CommonSourceFileFormat FileFormat { get; init; } = CommonSourceFileFormat.Auto;

    /// <summary>The global file format. Auto falls back to <see cref="FileFormat"/> and then the path.</summary>
    public CommonSourceFileFormat GlobalFileFormat { get; init; } = CommonSourceFileFormat.Auto;

    /// <summary>The local file format. Auto falls back to <see cref="FileFormat"/> and then the path.</summary>
    public CommonSourceFileFormat LocalFileFormat { get; init; } = CommonSourceFileFormat.Auto;

    /// <summary>The specific file format. Auto falls back to <see cref="FileFormat"/> and then the path.</summary>
    public CommonSourceFileFormat SpecificFileFormat { get; init; } = CommonSourceFileFormat.Auto;

    /// <summary>Property naming policy shared by the YAML file layers.</summary>
    public JsonNamingPolicy? YamlPropertyNamingPolicy { get; init; }

    /// <summary>SharpYaml serializer metadata and behavior shared by the YAML file layers.</summary>
    public YamlSerializerOptions? YamlSerializerOptions { get; init; }

    /// <summary>Backup and retry settings shared by the helper-created file resources.</summary>
    public FileResourceOptions? FileResourceOptions { get; init; }

    /// <summary>Environment variables provider override.</summary>
    public Func<IEnumerable<KeyValuePair<string, string?>>>? EnvironmentVariables { get; init; }
}

/// <summary>Composes the conventional layered source layout on a model registration.</summary>
public static class CommonSourcePreset
{
    /// <summary>Registers the conventional global and local JSON files with optional overlays.</summary>
    public static void UseCommonSources<TModel>(
        this ConfiglueModelBuilder<TModel> model,
        string applicationId,
        string? specificFilePath = null,
        string? environmentPrefix = null,
        CommonSourceWriteLayer? writeLayer = null
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        model.UseCommonSources(
            new CommonSourceOptions
            {
                ApplicationId = applicationId,
                GlobalFileName = "settings.json",
                SpecificFilePath = specificFilePath,
                EnvironmentPrefix = environmentPrefix,
                WriteLayer = writeLayer,
            }
        );
    }

    /// <summary>Registers global, local, specific, then environment sources in precedence order.</summary>
    public static void UseCommonSources<TModel>(
        this ConfiglueModelBuilder<TModel> model,
        CommonSourceOptions options
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApplicationId);
        ValidateFileName(options.GlobalFileName);
        if (options.LocalFilePath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.LocalFilePath);
        }
        if (options.SpecificFilePath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.SpecificFilePath);
        }

        var globalPath = Path.Combine(
            ConfiglueStandardPaths.GetStandardSaveDirectory(options.ApplicationId),
            options.GlobalFileName
        );
        var localPath = Path.GetFullPath(
            options.LocalFilePath
                ?? Path.Combine(System.Environment.CurrentDirectory, options.GlobalFileName)
        );
        var specificPath = options.SpecificFilePath is null
            ? null
            : Path.GetFullPath(options.SpecificFilePath);

        var writeId = options.WriteLayer switch
        {
            CommonSourceWriteLayer.Global => "common.global",
            CommonSourceWriteLayer.Local => "common.local",
            CommonSourceWriteLayer.Specific when specificPath is not null => "common.specific",
            CommonSourceWriteLayer.Specific => throw new InvalidOperationException(
                "The specific common source write layer requires a specific file path."
            ),
            null when specificPath is not null => "common.specific",
            null => "common.local",
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };

        model.WriteRoute = StateWriteRoute.To(writeId);
        model.Sources(sources =>
        {
            AddFile(sources, "common.global", globalPath, 100, options, options.GlobalFileFormat);
            AddFile(sources, "common.local", localPath, 200, options, options.LocalFileFormat);
            if (specificPath is not null)
            {
                AddFile(
                    sources,
                    "common.specific",
                    specificPath,
                    300,
                    options,
                    options.SpecificFileFormat
                );
            }

            if (options.EnvironmentPrefix is { } prefix)
            {
                sources.FromEnvironment(
                    new EnvironmentSourceOptions
                    {
                        Id = "common.environment",
                        Prefix = prefix,
                        Priority = 400,
                        FallbackCondition = StateFallbackCondition.NotFound,
                        EnvironmentVariables = options.EnvironmentVariables,
                    }
                );
            }
        });
    }

    private static void AddFile(
        ConfiglueSourceSetBuilder sources,
        string id,
        string path,
        int priority,
        CommonSourceOptions commonOptions,
        CommonSourceFileFormat layerFormat
    )
    {
        switch (ResolveFileFormat(layerFormat, commonOptions.FileFormat, path))
        {
            case CommonSourceFileFormat.Yaml:
                sources.FromYamlFile(
                    new YamlFileSourceOptions
                    {
                        Id = id,
                        Path = path,
                        Priority = priority,
                        FallbackCondition = StateFallbackCondition.NotFound,
                        PropertyNamingPolicy = commonOptions.YamlPropertyNamingPolicy,
                        SerializerOptions = commonOptions.YamlSerializerOptions,
                        ResourceOptions = commonOptions.FileResourceOptions,
                    }
                );
                break;
            case CommonSourceFileFormat.Xml:
                sources.FromXmlFile(
                    new XmlFileSourceOptions
                    {
                        Id = id,
                        Path = path,
                        Priority = priority,
                        FallbackCondition = StateFallbackCondition.NotFound,
                        ResourceOptions = commonOptions.FileResourceOptions,
                    }
                );
                break;
            default:
                sources.FromJsonFile(
                    new JsonFileSourceOptions
                    {
                        Id = id,
                        Path = path,
                        Priority = priority,
                        FallbackCondition = StateFallbackCondition.NotFound,
                        SerializerOptions = commonOptions.SerializerOptions,
                        ResourceOptions = commonOptions.FileResourceOptions,
                    }
                );
                break;
        }
    }

    private static CommonSourceFileFormat ResolveFileFormat(
        CommonSourceFileFormat layerFormat,
        CommonSourceFileFormat defaultFormat,
        string path
    )
    {
        if (layerFormat != CommonSourceFileFormat.Auto)
        {
            return layerFormat;
        }

        if (defaultFormat != CommonSourceFileFormat.Auto)
        {
            return defaultFormat;
        }

        var extension = Path.GetExtension(path);
        if (
            extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)
        )
        {
            return CommonSourceFileFormat.Yaml;
        }

        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return CommonSourceFileFormat.Xml;
        }

        return CommonSourceFileFormat.Json;
    }

    private static void ValidateFileName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        const string portableInvalidCharacters = "<>:\"/\\|?*";
        if (
            Path.IsPathRooted(value)
            || value is "." or ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.IndexOfAny(portableInvalidCharacters.ToCharArray()) >= 0
            || value.Any(char.IsControl)
        )
        {
            throw new ArgumentException(
                "GlobalFileName must be a portable file name, not a path.",
                nameof(value)
            );
        }
    }
}
