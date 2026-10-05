namespace Configlue.Extensibility;

/// <summary>
/// Composes a file-backed serialized source owned by the standard Configlue layer.
/// </summary>
/// <remarks>
/// <para>
/// Codecs describe representation and resources describe storage; this is the upper
/// composition layer that combines them for file-backed sources. Format providers
/// (JSON, YAML, XML, MessagePack) contribute only their codec plus a
/// format-specific document/section adapter, while the standard layer owns the
/// physical <see cref="Resources.FileResource"/> construction, transformer
/// application, and source-identity completion.
/// </para>
/// <para>
/// Provider file-source helpers such as <c>FromYamlFile</c> or <c>FromXmlFile</c>
/// are thin adapters over this entry point: they validate their own options,
/// build their codec and section adapter, and delegate here instead of
/// constructing a <see cref="Resources.FileResource"/> themselves. The canonical
/// low-level <c>Resource + Codec</c> path remains <see cref="SerializedSource{T}"/>
/// for providers whose storage is not a file.
/// </para>
/// </remarks>
/// <remarks>Provider SPI: the standard file-source composition entry point. Hidden from ordinary
/// completion; application code uses provider registration helpers.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public static class FileSourceComposition
{
    /// <summary>
    /// Creates a file-backed source by composing a helper-created file resource with a
    /// codec and a format-specific document/section adapter.
    /// </summary>
    /// <typeparam name="TFragment">The generated fragment type.</typeparam>
    /// <param name="path">The backing file path.</param>
    /// <param name="backupSchema">The model identity. Retained for compatibility; it no longer affects the single backup location.</param>
    /// <param name="resourceOptions">Backup and retry settings for the helper-created file resource.</param>
    /// <param name="fixedResourceId">An optional stable physical identity for the resource.</param>
    /// <param name="hostPaths">Host-specific defaults. Retained for compatibility; host-specific placement no longer affects the single backup location.</param>
    /// <param name="ownResource">Registers the created file resource with the facade lifetime.</param>
    /// <param name="readOnly">Whether the source exposes no writer.</param>
    /// <param name="watchChanges">Whether the source watches the file for changes.</param>
    /// <param name="transformers">Byte transformers applied when reading and writing this source.</param>
    /// <param name="sectionAdapter">Wraps the file view in a format-specific document/section view,
    /// returning the resource to read and the writer to expose (or <c>null</c> when read-only).</param>
    /// <param name="codec">The codec that converts between bytes and state values.</param>
    /// <param name="codecContext">Additional codec context.</param>
    /// <param name="id">The caller-configured logical source ID, if any.</param>
    /// <param name="derivedId">The provider-derived logical source ID used when <paramref name="id"/> is null.</param>
    /// <param name="priority">Higher values are read first.</param>
    /// <param name="fallbackCondition">Read statuses that allow lower-priority sources to be tried.</param>
    /// <param name="explicitOnly">Whether this source is excluded from inferred ordinary write routing.</param>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
    public static StateSource<TFragment> Create<TFragment>(
        string path,
        StateSchemaMetadata? backupSchema,
        FileResourceOptions? resourceOptions,
        ResourceId? fixedResourceId,
        IConfiglueHostPaths hostPaths,
        Action<object> ownResource,
        bool readOnly,
        bool watchChanges,
        IReadOnlyList<IStateByteTransformer>? transformers,
        Func<
            IResourceReader,
            IResourceWriter?,
            ISourceWatcher?,
            (IResourceReader Resource, IResourceWriter? Writer)
        > sectionAdapter,
        StateCodecBinding codec,
        StateCodecContext codecContext,
        string? id,
        string derivedId,
        int priority,
        StateFallbackCondition fallbackCondition,
        bool explicitOnly
    )
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(hostPaths);
        ArgumentNullException.ThrowIfNull(ownResource);
        ArgumentNullException.ThrowIfNull(sectionAdapter);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentException.ThrowIfNullOrWhiteSpace(derivedId);

        // The model and host arguments are retained for provider-SPI compatibility only;
        // the simplified backup contract always resolves a single backup from the options.
        _ = backupSchema;
        _ = hostPaths;
        var file = new FileResource(path, resourceOptions, fixedResourceId);
        ownResource(file);

        IResourceReader resource = file;
        IResourceWriter? writer = readOnly ? null : file;
        if (transformers is { Count: > 0 })
        {
            var transformed = new TransformingResource(file, transformers);
            resource = transformed;
            writer = readOnly ? null : transformed.Writer;
        }

        ISourceWatcher? watcher = watchChanges ? file : null;
        var (sectionResource, sectionWriter) = sectionAdapter(resource, writer, watcher);
        var serialized = new SerializedSource<TFragment>(
            sectionResource,
            codec,
            codecContext,
            writer: sectionWriter,
            watcher: watcher
        );
        return ConfiglueSourceCompletion.WithDerivedIdentity(
            serialized,
            id,
            derivedId,
            priority,
            fallbackCondition,
            file.Path,
            fixedResourceId,
            explicitOnly
        );
    }
}
