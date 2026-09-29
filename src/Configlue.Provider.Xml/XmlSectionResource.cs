using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Configlue.Provider.Xml;

/// <summary>Exposes a nested XML element as a resource while preserving sibling elements.</summary>
public sealed class XmlSectionResource
    : IResourceReader,
        IPipelineResourceReader,
        IResourceWriter,
        IStateWatcher,
        IResourceIdentity,
        IResourceBatchParticipant,
        IResourceBackupRecovery
{
    private readonly IResourceReader _reader;
    private readonly IResourceWriter? _writer;
    private readonly IStateWatcher? _watcher;
    private readonly string[] _path;
    private readonly string _batchScope;
    private readonly ResourceId? _configuredResourceId;
    private readonly object _sectionCacheGate = new();
    private string? _cachedSectionRevision;
    private SubjectKey _cachedSectionKey;
    private ResourceReadResult _cachedSection;
    private bool _hasCachedSection;

    /// <summary>Creates an XML section resource over a resource with inferred write and watch capabilities.</summary>
    public XmlSectionResource(IResourceReader resource, string sectionPath)
        : this(resource, resource as IResourceWriter, sectionPath, resource as IStateWatcher) { }

    /// <summary>Creates an XML section resource with separate read, write, and watch capabilities.</summary>
    public XmlSectionResource(
        IResourceReader reader,
        IResourceWriter? writer,
        string sectionPath,
        IStateWatcher? watcher = null,
        ResourceId? resourceId = null
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);
        _reader = reader;
        _writer = writer;
        _watcher = watcher;
        _configuredResourceId = resourceId;
        ResourceId =
            resourceId
            ?? ResolveResourceId(writer, reader)
            ?? new ResourceId($"section:{Guid.NewGuid():N}");
        _path = ParsePath(sectionPath);
        _batchScope = "xml/" + string.Join("/", _path.Select(Uri.EscapeDataString));
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
            return ExtractSection(resource, context.Key);
        }
        catch (XmlException exception)
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
                        section = ExtractSection(candidate, context.Key);
                    }
                    catch (XmlException)
                    {
                        return false;
                    }

                    return section.Status == StateReadStatus.Success
                        && await validate(section, token).ConfigureAwait(false);
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return restored is { } result ? ExtractSection(result, context.Key) : null;
    }

    private ResourceReadResult ExtractSection(
        ResourceReadResult resource,
        SubjectKey subjectKey = default
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
                    && _cachedSectionKey == subjectKey
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
                _cachedSectionKey = subjectKey;
                _cachedSection = result;
                _hasCachedSection = true;
            }
        }

        return result;
    }

    private ResourceReadResult ExtractSectionCore(ResourceReadResult resource)
    {
        var document = LoadDocument(resource.Content);
        var current =
            document.Root ?? throw new XmlException("The XML resource has no root element.");
        foreach (var name in _path)
        {
            var matches = current
                .Elements()
                .Where(element => element.Name.LocalName == name)
                .Take(2)
                .ToArray();
            if (matches.Length == 0)
            {
                return ResourceReadResult.NotFound(resource.Revision);
            }

            if (matches.Length > 1)
            {
                throw new XmlException(
                    $"Section path '{string.Join(':', _path)}' is ambiguous at '{name}'."
                );
            }

            current = matches[0];
        }

        var payload = current.Elements().Take(2).ToArray();
        var value =
            payload.Length == 1 && payload[0].Name.LocalName == "configlue" ? payload[0] : current;
        return ResourceReadResult.Success(
            Encoding.UTF8.GetBytes(value.ToString(SaveOptions.DisableFormatting)),
            resource.Revision
        );
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
            _writer ?? throw new NotSupportedException("This XML section resource is read-only.");
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
                    var section = ExtractSection(current, context.Key);
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
        XDocument document;
        if (current.Status == StateReadStatus.Success)
        {
            document = LoadDocument(current.Content);
        }
        else if (current.Status == StateReadStatus.NotFound)
        {
            document = new XDocument(new XElement("configuration"));
        }
        else
        {
            throw new IOException("The XML resource is unavailable and cannot be updated safely.");
        }

        var root = document.Root ?? throw new XmlException("The XML resource has no root element.");
        var container = root;
        for (var index = 0; index < _path.Length - 1; index++)
        {
            var name = _path[index];
            var matches = container
                .Elements()
                .Where(element => element.Name.LocalName == name)
                .Take(2)
                .ToArray();
            if (matches.Length > 1)
            {
                throw new XmlException(
                    $"Section path '{string.Join(':', _path)}' is ambiguous at '{name}'."
                );
            }

            if (matches.Length == 0)
            {
                var child = new XElement(container.Name.Namespace + name);
                container.Add(child);
                container = child;
            }
            else
            {
                container = matches[0];
            }
        }

        var updatedSection = XElement.Parse(
            Encoding.UTF8.GetString(sectionContent.Span),
            LoadOptions.PreserveWhitespace
        );
        var existing = container
            .Elements()
            .Where(element => element.Name.LocalName == _path[^1])
            .Take(2)
            .ToArray();
        if (existing.Length > 1)
        {
            throw new XmlException(
                $"Section path '{string.Join(':', _path)}' is ambiguous at '{_path[^1]}'."
            );
        }

        if (existing.Length == 0)
        {
            container.Add(new XElement(container.Name.Namespace + _path[^1], updatedSection));
        }
        else
        {
            existing[0].RemoveNodes();
            existing[0].Add(updatedSection);
        }

        return Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
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

    private static string[] ParsePath(string sectionPath)
    {
        var path = sectionPath
            .Replace("__", ":", StringComparison.Ordinal)
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return path.Length == 0
            ? throw new ArgumentException(
                "The section path must contain at least one element name.",
                nameof(sectionPath)
            )
            : path;
    }

    private static XDocument LoadDocument(ReadOnlyMemory<byte> content)
    {
        using var memory = CreateReadOnlyStream(content);
        using var reader = XmlReader.Create(
            memory,
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }
        );
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
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
