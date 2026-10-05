using System.Runtime.InteropServices;
using System.Text;
using Configlue.Sources;
using SharpYaml.Model;

namespace Configlue.Provider.Yaml;

/// <summary>Exposes a nested YAML mapping as a resource while preserving sibling nodes.</summary>
/// <remarks>
/// A section is a logical view over one physical YAML document shared with sibling sections. The
/// logical schema supplied when writing a section belongs to the section payload and is never
/// written to the physical document's schema metadata. To declare the physical container schema,
/// set <see cref="ContainerSchema"/> explicitly; otherwise section writes leave the container's
/// existing schema metadata unchanged. Whole-document (root) views forward the logical schema
/// because for them the logical and physical schema coincide.
/// </remarks>
public sealed class YamlSectionResource
    : IResourceReader,
        IPipelineResourceReader,
        IResourceWriter,
        ISourceWatcher,
        ITryResourceIdentity,
        IResourceBatchParticipant,
        IContextualResourceBackupRecovery
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );
    private readonly IResourceReader _reader;
    private readonly IResourceWriter? _writer;
    private readonly ISourceWatcher? _watcher;
    private readonly string[] _path;
    private readonly string _batchScope;
    private readonly ResourceId? _configuredResourceId;
    private readonly Encoding? _textEncoding;
    private readonly YamlSchemaShape? _schemaShape;
    private readonly object _sectionCacheGate = new();
    private string? _cachedSectionRevision;
    private ResourceKey _cachedSectionKey;
    private RouteKey _cachedSectionRoute;
    private ResourceReadResult _cachedSection;
    private bool _hasCachedSection;

    /// <summary>Creates a YAML section resource over a resource with inferred write and watch capabilities.</summary>
    public YamlSectionResource(IResourceReader resource, string sectionPath)
        : this(resource, resource as IResourceWriter, sectionPath, resource as ISourceWatcher) { }

    /// <summary>Creates a YAML section resource with separate read, write, and watch capabilities.</summary>
    public YamlSectionResource(
        IResourceReader reader,
        IResourceWriter? writer,
        string sectionPath,
        ISourceWatcher? watcher = null,
        ResourceId? fixedResourceId = null,
        Encoding? textEncoding = null
    )
        : this(
            reader,
            writer,
            ParseSectionPath(sectionPath),
            watcher,
            fixedResourceId,
            textEncoding,
            schemaShape: null
        ) { }

    internal YamlSectionResource(
        IResourceReader reader,
        IResourceWriter? writer,
        string sectionPath,
        ISourceWatcher? watcher,
        ResourceId? fixedResourceId,
        Encoding? textEncoding,
        YamlSchemaShape? schemaShape
    )
        : this(
            reader,
            writer,
            ParseSectionPath(sectionPath),
            watcher,
            fixedResourceId,
            textEncoding,
            schemaShape
        ) { }

    private YamlSectionResource(
        IResourceReader reader,
        IResourceWriter? writer,
        string[] path,
        ISourceWatcher? watcher,
        ResourceId? fixedResourceId,
        Encoding? textEncoding,
        YamlSchemaShape? schemaShape
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
        _writer = writer;
        _watcher = watcher;
        _textEncoding = textEncoding;
        _configuredResourceId = fixedResourceId;
        _path = path;
        _schemaShape = schemaShape;
        _batchScope = SectionResourceOrchestration.BuildBatchScope("yaml", _path);
    }

    internal static YamlSectionResource CreateRoot(
        IResourceReader reader,
        IResourceWriter? writer,
        ISourceWatcher? watcher,
        ResourceId? fixedResourceId,
        Encoding? textEncoding,
        YamlSchemaShape? schemaShape
    ) => new(reader, writer, [], watcher, fixedResourceId, textEncoding, schemaShape);

    private static string[] ParseSectionPath(string sectionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);
        var path = sectionPath
            .Replace("__", ":")
            .Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static segment => segment.Trim())
            .Where(static segment => segment.Length > 0)
            .ToArray();
        if (path.Length == 0)
        {
            throw new ArgumentException(
                "The section path must contain at least one mapping key.",
                nameof(sectionPath)
            );
        }

        return path;
    }

    /// <summary>Whether a physical writer was supplied.</summary>
    public bool CanWrite => _writer is not null;

    /// <summary>
    /// The schema metadata of the physical YAML document that hosts this section. Set this only
    /// when the shared container itself is known to use that schema. When <see langword="null"/>
    /// (the default) section writes make no schema claim and preserve existing container metadata.
    /// This property has no effect on whole-document (root) views.
    /// </summary>
    public StateSchemaMetadata? ContainerSchema { get; init; }

    /// <inheritdoc />
    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        SectionResourceOrchestration.GetResourceIdOrThrow(
            _configuredResourceId,
            _writer,
            _reader,
            context
        );

    /// <inheritdoc />
    public bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId resourceId) =>
        SectionResourceOrchestration.TryGetResourceId(
            _configuredResourceId,
            _writer,
            _reader,
            context,
            out resourceId
        );

    /// <inheritdoc />
    public IResourceBatchWriter? BatchWriter => _writer as IResourceBatchWriter;

    /// <inheritdoc />
    public bool IsPipelineReadPreferred => false;

    /// <inheritdoc />
    public ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    ) =>
        SectionResourceOrchestration.ReadPipelineFromSectionAsync(
            () => ReadAsync(context, cancellationToken),
            cancellationToken
        );

    /// <inheritdoc />
    public bool AutomaticBackupRecoveryEnabled =>
        SectionResourceOrchestration.GetAutomaticBackupRecoveryEnabled(_reader);

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var resource = await _reader.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        try
        {
            return ExtractSection(resource, context);
        }
        catch (SharpYaml.YamlException exception)
        {
            exception.Data["Configlue.ObservedResourceRevision"] = resource.Revision;
            throw;
        }
        catch (DecoderFallbackException exception)
        {
            exception.Data["Configlue.ObservedResourceRevision"] = resource.Revision;
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask<ResourceReadResult?> TryRecoverLatestBackupAsync(
        string? expectedRevision,
        bool expectedMissing,
        Func<ResourceReadResult, CancellationToken, ValueTask<bool>> validate,
        CancellationToken cancellationToken = default
    ) =>
        TryRecoverLatestBackupAsync(
            ConfiglueResourceContext.Default,
            expectedRevision,
            expectedMissing,
            validate,
            cancellationToken
        );

    /// <inheritdoc />
    public ValueTask<ResourceReadResult?> TryRecoverLatestBackupAsync(
        ConfiglueResourceContext context,
        string? expectedRevision,
        bool expectedMissing,
        Func<ResourceReadResult, CancellationToken, ValueTask<bool>> validate,
        CancellationToken cancellationToken = default
    ) =>
        SectionResourceOrchestration.TryRecoverLatestBackupAsync(
            _reader,
            context,
            expectedRevision,
            expectedMissing,
            validate,
            candidate => ExtractSection(candidate, context),
            static exception => exception is SharpYaml.YamlException or DecoderFallbackException,
            cancellationToken
        );

    private ResourceReadResult ExtractSection(
        ResourceReadResult resource,
        ConfiglueResourceContext context
    )
    {
        if (SectionResourceOrchestration.TryPropagateNonSuccess(resource, out var propagated))
        {
            return propagated;
        }

        var revision = resource.Revision;
        if (revision is not null)
        {
            lock (_sectionCacheGate)
            {
                if (
                    _hasCachedSection
                    && _cachedSectionKey == context.ResourceKey
                    && _cachedSectionRoute == context.Route
                    && string.Equals(_cachedSectionRevision, revision, StringComparison.Ordinal)
                )
                {
                    return _cachedSection;
                }
            }
        }

        var result = ExtractSectionCore(resource);
        if (revision is not null)
        {
            lock (_sectionCacheGate)
            {
                _cachedSectionRevision = revision;
                _cachedSectionKey = context.ResourceKey;
                _cachedSectionRoute = context.Route;
                _cachedSection = result;
                _hasCachedSection = true;
            }
        }

        return result;
    }

    private ResourceReadResult ExtractSectionCore(ResourceReadResult resource)
    {
        var current = LoadRoot(resource.Content);
        if (_path.Length == 0)
        {
            return resource;
        }

        foreach (var name in _path)
        {
            if (current is not YamlMapping mapping)
            {
                throw new SharpYaml.YamlException(
                    $"Section path '{string.Join(":", _path)}' crosses a non-mapping value at '{name}'."
                );
            }

            if (!TryGet(mapping, name, out current!))
            {
                return ResourceReadResult.NotFound(resource.Revision);
            }
        }

        return ResourceReadResult.Success(SerializeNode(current), resource.Revision);
    }

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    ) =>
        SectionResourceOrchestration.WriteSectionWithMutationAsync(
            _reader,
            _writer,
            "This YAML section resource is read-only.",
            _path.Length == 0,
            ContainerSchema,
            request,
            context,
            CreateMutation(context, request),
            cancellationToken
        );

    /// <inheritdoc />
    public ResourceWriteMutation CreateMutation(
        ConfiglueResourceContext context,
        ResourceWriteRequest request
    )
    {
        var content = request.Content.ToArray();
        return SectionResourceOrchestration.CreateSectionMutation(
            request,
            context,
            _batchScope,
            ResolvePhysicalSchema(request),
            current => ExtractSection(current, context),
            current => ApplyToResource(current, content)
        );
    }

    private StateSchemaMetadata? ResolvePhysicalSchema(ResourceWriteRequest request) =>
        SectionResourceOrchestration.ResolvePhysicalSchema(
            _path.Length == 0,
            request.Schema,
            ContainerSchema
        );

    private ReadOnlyMemory<byte> ApplyToResource(
        ResourceReadResult current,
        ReadOnlyMemory<byte> sectionContent
    )
    {
        SectionResourceOrchestration.ThrowIfNotUpdatable(current, "YAML");

        return YamlDocumentEditor.Update(
            current.Status == StateReadStatus.Success
                ? current.Content
                : ReadOnlyMemory<byte>.Empty,
            sectionContent,
            _path,
            _schemaShape,
            _textEncoding
        );
    }

    /// <inheritdoc />
    public ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    ) =>
        SectionResourceOrchestration.WaitForChangeAsync(
            _watcher,
            ReadAsync,
            context,
            observedRevision,
            cancellationToken
        );

    private YamlElement LoadRoot(ReadOnlyMemory<byte> content)
    {
        var text = _textEncoding is null
            ? StrictUtf8.GetString(content.ToArray())
            : DecodeWithEncoding(content, _textEncoding);
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text.Substring(1);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new YamlMapping();
        }

        using var reader = new StringReader(text);
        var stream = YamlStream.Load(reader, null);
        if (stream.Count != 1)
        {
            throw new SharpYaml.YamlException(
                "A Configlue YAML resource must contain exactly one document."
            );
        }

        var root =
            stream[0].Contents
            ?? throw new SharpYaml.YamlException("A YAML document cannot be empty.");
        ValidateUniqueKeys(root);
        return root;
    }

    private static void ValidateUniqueKeys(YamlElement? element)
    {
        if (element is null)
        {
            return;
        }

        if (element is YamlMapping mapping)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in mapping)
            {
                if (pair.Key is YamlValue scalar && !keys.Add(scalar.Value))
                {
                    throw new SharpYaml.YamlException(
                        $"Duplicate YAML mapping key '{scalar.Value}' is ambiguous for section updates."
                    );
                }

                ValidateUniqueKeys(pair.Value);
            }

            return;
        }

        if (element is YamlSequence sequence)
        {
            foreach (var child in sequence)
            {
                ValidateUniqueKeys(child);
            }
        }
    }

    private static bool TryGet(YamlMapping mapping, string name, out YamlElement value) =>
        mapping.TryGetValue(name, out value!);

    private ReadOnlyMemory<byte> SerializeNode(YamlElement node)
    {
        var stream = new YamlStream(null);
        var document = new YamlDocument { Contents = node };
        stream.Add(document);
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        stream.WriteTo(writer, false);
        return (_textEncoding ?? Encoding.UTF8).GetBytes(writer.ToString());
    }

    private static string DecodeWithEncoding(ReadOnlyMemory<byte> content, Encoding encoding)
    {
        using var stream = CreateReadOnlyStream(content);
        using var reader = new StreamReader(
            stream,
            encoding,
            detectEncodingFromByteOrderMarks: true
        );
        return reader.ReadToEnd();
    }

    private static MemoryStream CreateReadOnlyStream(ReadOnlyMemory<byte> content)
    {
        if (MemoryMarshal.TryGetArray(content, out var segment) && segment.Array is not null)
        {
            return new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false);
        }

        return new MemoryStream(content.ToArray(), writable: false);
    }
}
