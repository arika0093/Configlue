using System.Runtime.InteropServices;
using System.Text;
using Configlue.Sources;
using SharpYaml.Model;

namespace Configlue.Provider.Yaml;

/// <summary>Exposes a nested YAML mapping as a resource while preserving sibling nodes.</summary>
public sealed class YamlSectionResource
    : IResourceReader,
        IPipelineResourceReader,
        IResourceWriter,
        ISourceWatcher,
        IResourceIdentity,
        IResourceBatchParticipant,
        IResourceBackupRecovery
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
    private readonly byte[] _schemaShape;
    private readonly object _sectionCacheGate = new();
    private string? _cachedSectionRevision;
    private SubjectKey _cachedSectionKey;
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
        ResourceId? resourceId = null,
        Encoding? textEncoding = null
    )
        : this(reader, writer, ParseSectionPath(sectionPath), watcher, resourceId, textEncoding, [])
    { }

    internal YamlSectionResource(
        IResourceReader reader,
        IResourceWriter? writer,
        string sectionPath,
        ISourceWatcher? watcher,
        ResourceId? resourceId,
        Encoding? textEncoding,
        byte[] schemaShape
    )
        : this(
            reader,
            writer,
            ParseSectionPath(sectionPath),
            watcher,
            resourceId,
            textEncoding,
            schemaShape
        ) { }

    private YamlSectionResource(
        IResourceReader reader,
        IResourceWriter? writer,
        string[] path,
        ISourceWatcher? watcher,
        ResourceId? resourceId,
        Encoding? textEncoding,
        byte[] schemaShape
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
        _writer = writer;
        _watcher = watcher;
        _textEncoding = textEncoding;
        _configuredResourceId = resourceId;
        _path = path;
        _schemaShape = schemaShape;
        ResourceId =
            resourceId
            ?? ResolveResourceId(writer, reader)
            ?? new ResourceId($"section:{Guid.NewGuid():N}");
        _batchScope = "yaml/" + string.Join("/", _path.Select(Uri.EscapeDataString));
    }

    private static ResourceId? ResolveResourceId(IResourceWriter? writer, IResourceReader reader)
    {
        var context = ConfiglueResourceContext.Default;
        if (
            writer is IResourceIdentity writerIdentity
            && writerIdentity.TryGetResourceId(context, out var writerResourceId)
        )
        {
            return writerResourceId;
        }

        return
            reader is IResourceIdentity readerIdentity
            && readerIdentity.TryGetResourceId(context, out var readerResourceId)
            ? readerResourceId
            : null;
    }

    internal static YamlSectionResource CreateRoot(
        IResourceReader reader,
        IResourceWriter? writer,
        ISourceWatcher? watcher,
        ResourceId? resourceId,
        Encoding? textEncoding,
        byte[] schemaShape
    ) => new(reader, writer, [], watcher, resourceId, textEncoding, schemaShape);

    private static string[] ParseSectionPath(string sectionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);
        var path = sectionPath
            .Replace("__", ":", StringComparison.Ordinal)
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

    /// <inheritdoc />
    public ResourceId ResourceId { get; }

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        TryGetResourceId(context, out var resourceId) ? resourceId : ResourceId;

    /// <inheritdoc />
    public bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId resourceId)
    {
        if (_configuredResourceId is { } configuredResourceId)
        {
            resourceId = configuredResourceId;
            return true;
        }

        if (
            _writer is IResourceIdentity writerIdentity
            && writerIdentity.TryGetResourceId(context, out resourceId)
        )
        {
            return true;
        }

        if (
            _reader is IResourceIdentity readerIdentity
            && readerIdentity.TryGetResourceId(context, out resourceId)
        )
        {
            return true;
        }

        resourceId = ResourceId;
        return true;
    }

    /// <inheritdoc />
    public IResourceBatchWriter? BatchWriter => _writer as IResourceBatchWriter;

    /// <inheritdoc />
    public bool IsPipelineReadPreferred => false;

    /// <inheritdoc />
    public async ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        CancellationToken cancellationToken = default
    )
    {
        var result = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return await PipelineResourceReader
            .FromMemoryAsync(result, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var result = await ReadAsync(context, cancellationToken).ConfigureAwait(false);
        return await PipelineResourceReader
            .FromMemoryAsync(result, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool AutomaticBackupRecoveryEnabled =>
        _reader is IResourceBackupRecovery recovery && recovery.AutomaticBackupRecoveryEnabled;

    /// <inheritdoc />
    public ValueTask<ResourceReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(ConfiglueResourceContext.Default, cancellationToken);

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
    public async ValueTask<ResourceReadResult?> TryRecoverLatestBackupAsync(
        ConfiglueResourceContext context,
        string? expectedRevision,
        bool expectedMissing,
        Func<ResourceReadResult, CancellationToken, ValueTask<bool>> validate,
        CancellationToken cancellationToken = default
    )
    {
        if (
            _reader is not IResourceBackupRecovery recovery
            || !recovery.AutomaticBackupRecoveryEnabled
        )
        {
            return null;
        }

        var restored = await recovery
            .TryRecoverLatestBackupAsync(
                context,
                expectedRevision,
                expectedMissing,
                async (candidate, token) =>
                {
                    ResourceReadResult section;
                    try
                    {
                        section = ExtractSection(candidate, context);
                    }
                    catch (SharpYaml.YamlException)
                    {
                        return false;
                    }
                    catch (DecoderFallbackException)
                    {
                        return false;
                    }

                    return section.Status == StateReadStatus.Success
                        && await validate(section, token).ConfigureAwait(false);
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return restored is { } result ? ExtractSection(result, context) : null;
    }

    private ResourceReadResult ExtractSection(
        ResourceReadResult resource,
        ConfiglueResourceContext context
    )
    {
        if (resource.Status != StateReadStatus.Success)
        {
            return new ResourceReadResult(resource.Status, default, resource.Revision);
        }

        var revision = resource.Revision;
        if (revision is not null)
        {
            lock (_sectionCacheGate)
            {
                if (
                    _hasCachedSection
                    && _cachedSectionKey == context.Key
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
                _cachedSectionKey = context.Key;
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
                    $"Section path '{string.Join(':', _path)}' crosses a non-mapping value at '{name}'."
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
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    ) => WriteAsync(ConfiglueResourceContext.Default, request, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var writer =
            _writer ?? throw new NotSupportedException("This YAML section resource is read-only.");
        var current = await _reader.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        var updated = CreateMutation(context, request).Apply(current);
        return await writer
            .WriteAsync(
                context,
                new ResourceWriteRequest(
                    updated,
                    Condition: RevisionCondition.FromRevision(current.Revision),
                    Schema: request.Schema
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ResourceWriteMutation CreateMutation(ResourceWriteRequest request) =>
        CreateMutation(ConfiglueResourceContext.Default, request);

    /// <inheritdoc />
    public ResourceWriteMutation CreateMutation(
        ConfiglueResourceContext context,
        ResourceWriteRequest request
    )
    {
        var content = request.Content.ToArray();
        return new ResourceWriteMutation(
            request.Condition.IsMustNotExist ? RevisionCondition.None : request.Condition,
            request.Schema,
            current =>
            {
                if (!request.Condition.IsNone)
                {
                    var section = ExtractSection(current, context);
                    if (
                        !request.Condition.IsSatisfiedBy(
                            section.Revision,
                            section.Status != StateReadStatus.NotFound
                        )
                    )
                    {
                        throw new StateConflictException("The section changed after it was read.");
                    }
                }
                return ApplyToResource(current, content);
            },
            scope: _batchScope,
            canCompose: true,
            context: context
        );
    }

    private ReadOnlyMemory<byte> ApplyToResource(
        ResourceReadResult current,
        ReadOnlyMemory<byte> sectionContent
    )
    {
        if (current.Status is not (StateReadStatus.Success or StateReadStatus.NotFound))
        {
            throw new IOException("The YAML resource is unavailable and cannot be updated safely.");
        }

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
        string? observedRevision,
        CancellationToken cancellationToken = default
    ) => WaitForChangeAsync(ConfiglueResourceContext.Default, observedRevision, cancellationToken);

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        if (_watcher is not null)
        {
            await _watcher
                .WaitForChangeAsync(context, observedRevision, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await ReadAsync(context, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current.Revision, observedRevision, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private YamlElement LoadRoot(ReadOnlyMemory<byte> content)
    {
        var text = _textEncoding is null
            ? StrictUtf8.GetString(content.Span)
            : DecodeWithEncoding(content, _textEncoding);
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
