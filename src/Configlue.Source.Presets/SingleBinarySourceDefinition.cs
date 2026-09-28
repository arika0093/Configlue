using Configlue.Provider.Json;
using Configlue.Resource.Zip;
using Configlue.State;
using Configlue.Transformer.AES;

namespace Configlue.Source.Presets;

internal sealed class SingleBinarySourceDefinition(
    string path,
    string entryName,
    FileResourceOptions? resourceOptions,
    SingleBinaryEncryption? encryption,
    int priority
) : IConfiglueSourceDefinition
{
    public StateSource<TFragment> Create<TFragment>(
        ConfiglueModelSchema modelSchema,
        IServiceProvider? serviceProvider,
        Action<IDisposable> ownResource
    )
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(modelSchema);
        ArgumentNullException.ThrowIfNull(ownResource);
        var resource = SingleBinarySourceFactory.CreateEntryResource(
            path,
            entryName,
            resourceOptions,
            encryption,
            ownResource
        );
        var converter =
            TFragment.JsonConverter
            ?? throw new InvalidOperationException(
                $"Generated JSON metadata is unavailable for fragment '{typeof(TFragment)}'."
            );
        var sourceId = SingleBinarySourceFactory.GetSourceId(entryName);
        return SerializedStateSource.FromResource<TFragment>(
            sourceId,
            resource,
            JsonStateCodec<TFragment>.FromConverter(converter),
            priority,
            physicalOrigin: path
        );
    }
}

internal static class SingleBinarySourceFactory
{
    public static SingleBinaryEntryResource CreateEntryResource(
        string path,
        string entryName,
        FileResourceOptions? resourceOptions,
        SingleBinaryEncryption? encryption,
        Action<IDisposable> ownResource
    )
    {
        var file = new FileResource(path, resourceOptions);
        ownResource(file);
        IResourceReader reader = file;
        IResourceBatchWriter? batchWriter = file;
        IStateWatcher watcher = file;
        if (encryption is not null)
        {
            var transformer = encryption.CreateTransformer();
            if (encryption.OwnsTransformer && transformer is IDisposable disposable)
            {
                ownResource(disposable);
            }

            var transforming = new TransformingResource(file, [transformer]);
            reader = transforming;
            batchWriter =
                transforming.Writer as IResourceBatchWriter
                ?? throw new InvalidOperationException(
                    "The encrypted archive resource does not support batch writes."
                );
        }

        return new SingleBinaryEntryResource(
            new ZipEntryResource(reader, batchWriter, entryName, watcher)
        );
    }

    public static StateSource<ConfiglueProfileCatalog> CreateProfileCatalog(
        string path,
        string modelKey,
        FileResourceOptions? resourceOptions,
        SingleBinaryEncryption? encryption,
        int priority,
        Action<IDisposable> ownResource
    )
    {
        var entryName = $"models/{Escape(modelKey)}/profile-catalog/catalog.json";
        var resource = CreateEntryResource(
            path,
            entryName,
            resourceOptions,
            encryption,
            ownResource
        );
        var codec = new JsonStateCodec<ConfiglueProfileCatalog>(
            SingleBinaryJsonContext.Default.ConfiglueProfileCatalog
        );
        return SerializedStateSource.FromResource<ConfiglueProfileCatalog>(
            GetSourceId(entryName),
            resource,
            codec,
            priority,
            physicalOrigin: path
        );
    }

    public static string GetModelEntryName(string modelKey, bool isProfile, string optionsName)
    {
        var category = isProfile ? "profiles" : "options";
        var name = optionsName.Length == 0 ? "default" : optionsName;
        return $"models/{Escape(modelKey)}/{category}/{Escape(name)}.json";
    }

    public static string GetSourceId(string entryName) => "single-binary:" + entryName;

    private static string Escape(string value)
    {
        var escaped = Uri.EscapeDataString(value);
        return escaped is "." or ".."
            ? escaped.Replace(".", "%2E", StringComparison.Ordinal)
            : escaped;
    }
}
