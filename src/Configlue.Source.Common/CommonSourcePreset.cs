using System.CommandLine;
using System.Text.Json;
using Configlue;
using Configlue.Provider.Json;
using Configlue.Source.CommandLine;
using Configlue.Source.Environment;

namespace Configlue.Source.Common;

/// <summary>The layer selected as the normal write destination.</summary>
public enum CommonSourceWriteLayer
{
    /// <summary>Write to the global per-user file.</summary>
    Global,

    /// <summary>Write to the local file.</summary>
    Local,

    /// <summary>Write to the explicitly selected file.</summary>
    Specific,

    /// <summary>Selects a destination from enabled file layers by priority and current accessibility.</summary>
    BestAvailable,
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

    /// <summary>The application's existing parse result; null omits command-line member overrides.</summary>
    public ParseResult? CommandLineParseResult { get; init; }

    /// <summary>Maps command-line options and arguments to model paths.</summary>
    public Action<CommandLineMappingBuilder>? ConfigureCommandLineMappings { get; init; }

    /// <summary>The selected writable file layer. Read-only layers remain overlays.</summary>
    public CommonSourceWriteLayer WriteLayer { get; init; } = CommonSourceWriteLayer.Global;

    /// <summary>Explicit write-selection priority for the global file. Higher values win.</summary>
    public int GlobalWritePriority { get; init; }

    /// <summary>Explicit write-selection priority for the local file. Higher values win.</summary>
    public int LocalWritePriority { get; init; }

    /// <summary>Explicit write-selection priority for the specific file. Higher values win.</summary>
    public int SpecificWritePriority { get; init; }

    /// <summary>JSON serialization and property naming options shared by the file layers.</summary>
    public JsonSerializerOptions? SerializerOptions { get; init; }

    /// <summary>Backup and retry settings shared by the helper-created file resources.</summary>
    public FileResourceOptions? FileResourceOptions { get; init; }

    /// <summary>Disables the global file layer.</summary>
    public bool EnableGlobalFile { get; init; } = true;

    /// <summary>Disables the local file layer.</summary>
    public bool EnableLocalFile { get; init; } = true;

    /// <summary>Disables the specific file layer.</summary>
    public bool EnableSpecificFile { get; init; } = true;

    /// <summary>Disables the environment layer.</summary>
    public bool EnableEnvironment { get; init; } = true;

    /// <summary>Disables command-line member overrides.</summary>
    public bool EnableCommandLine { get; init; } = true;

    /// <summary>Environment variables provider override.</summary>
    public Func<IEnumerable<KeyValuePair<string, string?>>>? EnvironmentVariables { get; init; }
}

/// <summary>Composes the conventional layered source layout on a model registration.</summary>
public static class CommonSourcePreset
{
    /// <summary>Registers global, local, specific, then environment sources in precedence order.</summary>
    /// <remarks>The selected specific file path is a separate input from command-line member overrides.</remarks>
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
            CommonSourceWriteLayer.BestAvailable => SelectBestAvailableWriteLayer(
                options,
                globalPath,
                localPath,
                specificPath
            ),
            CommonSourceWriteLayer.Global when options.EnableGlobalFile => "common.global",
            CommonSourceWriteLayer.Local when options.EnableLocalFile => "common.local",
            CommonSourceWriteLayer.Specific
                when options.EnableSpecificFile && specificPath is not null => "common.specific",
            _ => throw new InvalidOperationException(
                "The selected common source write layer is disabled or has no path."
            ),
        };

        model.WriteRoute = StateWriteRoute.To(writeId);
        model.Sources(sources =>
        {
            if (options.EnableGlobalFile)
            {
                AddFile(sources, "common.global", globalPath, 100, writeId, options);
            }

            if (options.EnableLocalFile)
            {
                AddFile(sources, "common.local", localPath, 200, writeId, options);
            }

            if (options.EnableSpecificFile && specificPath is not null)
            {
                AddFile(sources, "common.specific", specificPath, 300, writeId, options);
            }

            if (options.EnableEnvironment && options.EnvironmentPrefix is { } prefix)
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

            if (options.EnableCommandLine && options.CommandLineParseResult is { } parseResult)
            {
                var configureMappings =
                    options.ConfigureCommandLineMappings
                    ?? throw new InvalidOperationException(
                        "ConfigureCommandLineMappings is required when a parse result is supplied."
                    );
                sources.FromCommandLine(
                    new CommandLineSourceOptions
                    {
                        Id = "common.commandLine",
                        ParseResult = parseResult,
                        Priority = 500,
                        FallbackCondition = StateFallbackCondition.NotFound,
                    },
                    configureMappings
                );
            }
        });
    }

    private static string SelectBestAvailableWriteLayer(
        CommonSourceOptions options,
        string globalPath,
        string localPath,
        string? specificPath
    )
    {
        var candidates = new List<(string Id, string Path, int Priority, int Index)>(3);
        if (options.EnableGlobalFile)
        {
            candidates.Add(
                ("common.global", globalPath, options.GlobalWritePriority, candidates.Count)
            );
        }
        if (options.EnableLocalFile)
        {
            candidates.Add(
                ("common.local", localPath, options.LocalWritePriority, candidates.Count)
            );
        }
        if (options.EnableSpecificFile && specificPath is not null)
        {
            candidates.Add(
                ("common.specific", specificPath, options.SpecificWritePriority, candidates.Count)
            );
        }

        var selected = candidates
            .Select(candidate =>
                (
                    candidate.Id,
                    candidate.Path,
                    candidate.Priority,
                    candidate.Index,
                    CanWriteFile: CanWriteFile(candidate.Path),
                    CanWriteDirectory: CanWriteDirectory(candidate.Path)
                )
            )
            .OrderByDescending(static candidate => candidate.Priority)
            .ThenByDescending(static candidate => candidate.CanWriteFile)
            .ThenByDescending(static candidate => candidate.CanWriteDirectory)
            .ThenBy(static candidate => candidate.Index)
            .FirstOrDefault();

        if (selected.Id is null)
        {
            throw new InvalidOperationException(
                "No file layer is enabled for the common source write destination."
            );
        }

        EnsureSelectedDirectoryIsWritable(selected.Path);
        return selected.Id;
    }

    private static bool CanWriteFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.None
            );
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool CanWriteDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            using var probe = CreateWriteProbe(directory);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void EnsureSelectedDirectoryIsWritable(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            directory = System.Environment.CurrentDirectory;
        }

        try
        {
            Directory.CreateDirectory(directory);
            using var probe = CreateWriteProbe(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The selected common source write directory '{directory}' is not writable.",
                exception
            );
        }
    }

    private static FileStream CreateWriteProbe(string directory) =>
        new(
            Path.Combine(directory, Path.GetRandomFileName()),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose
        );

    private static void AddFile(
        ConfiglueSourceSetBuilder sources,
        string id,
        string path,
        int priority,
        string writeId,
        CommonSourceOptions commonOptions
    ) =>
        sources.FromJsonFile(
            new JsonFileSourceOptions
            {
                Id = id,
                Path = path,
                Priority = priority,
                FallbackCondition = StateFallbackCondition.NotFound,
                ReadOnly = !string.Equals(id, writeId, StringComparison.Ordinal),
                SerializerOptions = commonOptions.SerializerOptions,
                ResourceOptions = commonOptions.FileResourceOptions,
            }
        );

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
