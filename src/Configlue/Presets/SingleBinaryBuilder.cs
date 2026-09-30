using System.Text.Json.Serialization;
using Configlue.CompilerServices;
using Configlue.Provider.Json;

namespace Configlue.Source.Presets;

/// <summary>Builds model sources stored as entries in one local ZIP-backed file.</summary>
public sealed class SingleBinaryBuilder
{
    private readonly ConfiglueBuilder _configlue;
    private readonly HashSet<Type> _profileModels = [];
    private readonly Dictionary<string, Type> _storageKeyOwners = new(StringComparer.Ordinal);
    private string? _path;
    private FileResourceOptions? _resourceOptions;
    private SingleBinaryEncryption? _encryption;
    private string _defaultProfileName = "default";
    private int _priority;
    private bool _profilesEnabled;
    private bool _hasAddedModel;

    internal SingleBinaryBuilder(ConfiglueBuilder configlue)
    {
        _configlue = configlue;
    }

    /// <summary>Uses one local file for all models registered in this scope.</summary>
    public SingleBinaryBuilder WithLocal(string filepath)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(filepath);
        _path = Path.GetFullPath(filepath);
        return this;
    }

    /// <summary>Stores the archive in the host-wide application directory.</summary>
    public SingleBinaryBuilder WithHostGlobal(string applicationId, string filename = "state.bin")
    {
        EnsureMutable();
        ValidateStandardFilename(filename);
        _path = Path.GetFullPath(
            Path.Combine(
                _configlue.ResolveStandardDirectory(
                    ConfiglueStandardLocation.HostGlobal,
                    applicationId
                ),
                filename
            )
        );
        return this;
    }

    /// <summary>Stores the archive in the persistent per-user application directory.</summary>
    public SingleBinaryBuilder WithUserGlobal(string applicationId, string filename = "state.bin")
    {
        EnsureMutable();
        ValidateStandardFilename(filename);
        _path = Path.GetFullPath(
            Path.Combine(
                _configlue.ResolveStandardDirectory(
                    ConfiglueStandardLocation.UserGlobal,
                    applicationId
                ),
                filename
            )
        );
        return this;
    }

    /// <summary>Sets the options used by the underlying local file resource.</summary>
    public SingleBinaryBuilder FileResourceOptions(FileResourceOptions options)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(options);
        _resourceOptions = options;
        return this;
    }

    /// <summary>Encrypts the complete ZIP archive using a caller-owned transformer.</summary>
    public SingleBinaryBuilder WithEncryption(IStateByteTransformer transformer)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(transformer);
        var synchronized = new SingleBinarySynchronizedTransformer(transformer);
        _encryption = new SingleBinaryEncryption
        {
            CreateTransformer = () => synchronized,
            OwnsTransformer = false,
        };
        return this;
    }

    /// <summary>Encrypts the complete archive using a newly created owned transformer per source.</summary>
    public SingleBinaryBuilder WithEncryption(Func<IStateByteTransformer> transformerFactory)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(transformerFactory);
        _encryption = new SingleBinaryEncryption
        {
            CreateTransformer = transformerFactory,
            OwnsTransformer = true,
        };
        return this;
    }

    /// <summary>Enables persisted profiles and stores each model's profile catalog in the archive.</summary>
    public SingleBinaryBuilder WithProfiles(string defaultProfileName = "default")
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultProfileName);
        _defaultProfileName = defaultProfileName;
        _profilesEnabled = true;
        return this;
    }

    /// <summary>Sets the source priority used for every entry in this archive.</summary>
    public SingleBinaryBuilder Priority(int priority)
    {
        EnsureMutable();
        _priority = priority;
        return this;
    }

    /// <summary>Adds one model, optionally selecting a stable storage key independent of its CLR name.</summary>
    public void Add<TModel>(
        Action<ConfiglueModelBuilder<TModel>>? configure = null,
        string? storageKey = null
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        EnsureFileConfigured();
        if (storageKey is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        }

        var modelKey = storageKey ?? GetDefaultStorageKey(typeof(TModel));
        var path = _path!;
        var resourceOptions = _resourceOptions;
        var encryption = _encryption;
        var priority = _priority;
        var defaultProfileName = _defaultProfileName;
        if (
            _storageKeyOwners.TryGetValue(modelKey, out var existingModelType)
            && existingModelType != typeof(TModel)
        )
        {
            throw new ArgumentException(
                $"Storage key '{modelKey}' is already assigned to model '{existingModelType}'.",
                nameof(storageKey)
            );
        }

        var registerProfiles = _profilesEnabled && !_profileModels.Contains(typeof(TModel));

        _configlue.Add<TModel>(model =>
        {
            configure?.Invoke(model);
            var baseStateName = model.StateName;
            if (registerProfiles)
            {
                model.EnableProfiles(
                    (_, ownResource) =>
                        SingleBinarySourceFactory.CreateProfileCatalog(
                            path,
                            modelKey,
                            resourceOptions,
                            encryption,
                            priority,
                            ownResource,
                            _configlue.HostPaths
                        ),
                    defaultProfileName
                );
            }

            model.ConfigureSources(registration =>
            {
                var isProfile =
                    registerProfiles
                    && !string.Equals(
                        registration.StateName,
                        baseStateName,
                        StringComparison.Ordinal
                    );
                var entryName = SingleBinarySourceFactory.GetModelEntryName(
                    modelKey,
                    isProfile,
                    registration.StateName
                );
                ((IConfiglueSourceRegistrationSink)registration.Sources)
                    .Add(
                        new SingleBinarySourceDefinition(
                            path,
                            entryName,
                            resourceOptions,
                            encryption,
                            priority
                        )
                    )
                    .Priority(priority);
            });
        });
        _storageKeyOwners.TryAdd(modelKey, typeof(TModel));
        if (registerProfiles)
        {
            _profileModels.Add(typeof(TModel));
        }
        _hasAddedModel = true;
    }

    internal void EnsureComplete()
    {
        EnsureFileConfigured();
        if (!_hasAddedModel)
        {
            throw new InvalidOperationException(
                "UseSingleBinary must add at least one model in its configuration callback."
            );
        }
    }

    private void EnsureMutable()
    {
        if (_hasAddedModel)
        {
            throw new InvalidOperationException(
                "Single-binary options must be configured before adding models."
            );
        }
    }

    private void EnsureFileConfigured()
    {
        if (_path is null)
        {
            throw new InvalidOperationException(
                "Configure the archive path with WithLocal, WithUserGlobal, or WithHostGlobal before adding models."
            );
        }
    }

    private static void ValidateStandardFilename(string filename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        if (
            filename is "." or ".."
            || Path.IsPathRooted(filename)
            || !string.Equals(Path.GetFileName(filename), filename, StringComparison.Ordinal)
        )
        {
            throw new ArgumentException(
                "The archive name must be a file name, not a path.",
                nameof(filename)
            );
        }
    }

    private static string GetDefaultStorageKey(Type modelType) =>
        modelType.FullName ?? modelType.Name;
}

/// <summary>Registers one ZIP-backed binary store.</summary>
public static class SingleBinaryBuilderExtensions
{
    /// <summary>Configures a single local ZIP-backed file and adds its model sources.</summary>
    public static void UseSingleBinary(
        this ConfiglueBuilder configlue,
        Action<SingleBinaryBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(configlue);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new SingleBinaryBuilder(configlue);
        configure(builder);
        builder.EnsureComplete();
    }
}

internal sealed record SingleBinaryEncryption
{
    public required Func<IStateByteTransformer> CreateTransformer { get; init; }
    public required bool OwnsTransformer { get; init; }
}

[JsonSerializable(typeof(ConfiglueProfileCatalog))]
internal partial class SingleBinaryJsonContext : JsonSerializerContext { }
