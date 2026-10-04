using Configlue.Provider.Json;
using Configlue.Resource.Zip;
using Configlue.Sources;
using Configlue.State;

namespace Configlue.Source.Presets;

internal sealed class SingleBinarySourceDefinition(
    string path,
    string entryName,
    FileResourceOptions? resourceOptions,
    SingleBinaryEncryption? encryption,
    int priority
) : IConfiglueSourceDefinition
{
    public ConfiglueSourceCreation<TFragment> Create<TFragment>(
        ConfiglueSourceCreationContext context
    )
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        return context.Complete(
            CreateSourceCore<TFragment>(context.ModelSchema, context.HostPaths, context.Own)
        );
    }

    private StateSource<TFragment> CreateSourceCore<TFragment>(
        ConfiglueModelSchema modelSchema,
        IConfiglueHostPaths hostPaths,
        Action<object> ownResource
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
            ownResource,
            hostPaths
        );
        var converter =
            ConfiglueJsonFragmentRegistry<TFragment>.Converter
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
        Action<object> ownResource,
        IConfiglueHostPaths hostPaths
    )
    {
        var file = new FileResource(path, resourceOptions, null, hostPaths);
        ownResource(file);
        IResourceReader reader = file;
        IResourceBatchWriter? batchWriter = file;
        ISourceWatcher watcher = file;
        if (encryption is not null)
        {
            var transformer = encryption.CreateTransformer();
            if (encryption.OwnsTransformer && transformer is IDisposable or IAsyncDisposable)
            {
                ownResource(transformer);
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
            new ZipEntryResource(
                reader,
                batchWriter,
                new ZipEntryResourceOptions
                {
                    EntryNameSelector = context =>
                        GetSubjectEntryName(entryName, context.ResourceKey),
                },
                entryName,
                watcher
            )
        );
    }

    public static StateSource<ConfiglueProfileCatalog> CreateProfileCatalog(
        string path,
        string modelKey,
        FileResourceOptions? resourceOptions,
        SingleBinaryEncryption? encryption,
        int priority,
        Action<object> ownResource,
        IConfiglueHostPaths hostPaths
    )
    {
        var entryName = $"models/{Escape(modelKey)}/profile-catalog/catalog.json";
        var resource = CreateEntryResource(
            path,
            entryName,
            resourceOptions,
            encryption,
            ownResource,
            hostPaths
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

    /// <summary>
    /// Builds the ZIP entry name for a model state.
    /// </summary>
    /// <remarks>
    /// The unnamed state (<paramref name="stateName"/> == "") owns
    /// <c>models/{modelKey}/options/default.json</c> for backward compatibility.
    /// The explicit named state <c>"default"</c> is stored separately at
    /// <c>models/{modelKey}/options/named/default.json</c> so the two states no
    /// longer overwrite the same entry. Before this fix both names mapped to the
    /// shared <c>options/default.json</c> entry and the last writer won; existing
    /// archives keep that shared entry as the unnamed state's value and nothing is
    /// auto-copied or auto-moved to the named entry. All other non-empty state names
    /// keep the existing <c>options/{escaped-name}.json</c> layout. State names are
    /// escaped with <see cref="Uri.EscapeDataString(string)"/>, so <c>'/'</c> becomes
    /// <c>%2F</c> and user names can never collide with the <c>named/</c> sub-hierarchy.
    /// The <c>profiles</c> category is unaffected.
    /// </remarks>
    public static string GetModelEntryName(string modelKey, bool isProfile, string stateName)
    {
        var category = isProfile ? "profiles" : "options";
        if (!isProfile)
        {
            if (stateName.Length == 0)
            {
                return $"models/{Escape(modelKey)}/{category}/{Escape("default")}.json";
            }

            if (string.Equals(stateName, "default", StringComparison.Ordinal))
            {
                return $"models/{Escape(modelKey)}/{category}/named/default.json";
            }

            return $"models/{Escape(modelKey)}/{category}/{Escape(stateName)}.json";
        }

        var name = stateName.Length == 0 ? "default" : stateName;
        return $"models/{Escape(modelKey)}/{category}/{Escape(name)}.json";
    }

    private static string GetSubjectEntryName(string entryName, ResourceKey subjectKey) =>
        subjectKey.IsDefault ? entryName : $"subjects/{Escape(subjectKey.Value)}/{entryName}";

    public static string GetSourceId(string entryName) => "single-binary:" + entryName;

    private static string Escape(string value)
    {
        var escaped = Uri.EscapeDataString(value);
        return escaped is "." or ".." ? escaped.Replace(".", "%2E") : escaped;
    }
}
