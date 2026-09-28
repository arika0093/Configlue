using System.Text.Json.Serialization;
using Configlue.Provider.Json;
using Configlue.Transformer.AES;

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
        _encryption = new SingleBinaryEncryption(() => synchronized, OwnsTransformer: false);
        return this;
    }

    /// <summary>Encrypts the complete ZIP archive using an AES key of 128, 192, or 256 bits.</summary>
    /// <remarks>
    /// The key memory is retained for creating named-option and profile sources. Keep it unchanged
    /// until no context can create additional runtimes from this registration.
    /// </remarks>
    public SingleBinaryBuilder WithAesKey(ReadOnlyMemory<byte> key)
    {
        EnsureMutable();
        if (key.Length is not (16 or 24 or 32))
        {
            throw new ArgumentException(
                "An AES key must contain 16, 24, or 32 bytes.",
                nameof(key)
            );
        }

        _encryption = new SingleBinaryEncryption(
            () => new AesGcmStateByteTransformer(key.Span),
            OwnsTransformer: true
        );
        return this;
    }

    /// <summary>Encrypts the complete ZIP archive using an AES-GCM key derived from a passphrase.</summary>
    public SingleBinaryBuilder WithPassphrase(string passphrase)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(passphrase);
        if (passphrase.Length == 0)
        {
            throw new ArgumentException("A passphrase cannot be empty.", nameof(passphrase));
        }

        _encryption = new SingleBinaryEncryption(
            () => new AesGcmPassphraseStateByteTransformer(passphrase),
            OwnsTransformer: true
        );
        return this;
    }

    /// <summary>Encrypts the complete archive using a passphrase.</summary>
    /// <remarks>This is a convenience alias for <see cref="WithPassphrase"/>.</remarks>
    public SingleBinaryBuilder WithEncrypted(string passphrase) => WithPassphrase(passphrase);

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
            var baseOptionsName = model.OptionsName;
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
                            ownResource
                        ),
                    defaultProfileName
                );
            }

            model.ConfigureSources(registration =>
            {
                var isProfile =
                    registerProfiles
                    && !string.Equals(
                        registration.OptionsName,
                        baseOptionsName,
                        StringComparison.Ordinal
                    );
                var entryName = SingleBinarySourceFactory.GetModelEntryName(
                    modelKey,
                    isProfile,
                    registration.OptionsName
                );
                registration
                    .Sources.Add(
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
                "Configure the archive file with WithLocal before adding models."
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

internal sealed record SingleBinaryEncryption(
    Func<IStateByteTransformer> CreateTransformer,
    bool OwnsTransformer
);

[JsonSerializable(typeof(ConfiglueProfileCatalog))]
internal partial class SingleBinaryJsonContext : JsonSerializerContext { }
