using System.Text.Json;

namespace Configlue.Provider.Json;

/// <summary>Exposes a nested JSON object as an independently revisioned resource view.</summary>
public sealed class JsonSectionResource
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
    private readonly byte[] _schemaShape;
    private readonly JsonSerializerOptions _serializerOptions;

    /// <summary>Creates a section resource over an existing JSON resource.</summary>
    /// <param name="resource">The physical resource containing the JSON document.</param>
    /// <param name="sectionPath">A colon- or double-underscore-separated path to the section.</param>
    /// <param name="serializerOptions">Options used to format newly created JSON structure.</param>
    /// <param name="resourceId">An optional stable identity for the physical resource.</param>
    public JsonSectionResource(
        IResourceReader resource,
        string sectionPath,
        JsonSerializerOptions? serializerOptions = null,
        ResourceId? resourceId = null
    )
        : this(
            resource,
            resource as IResourceWriter,
            sectionPath,
            resource as IStateWatcher,
            serializerOptions,
            resourceId
        ) { }

    /// <summary>Creates a section resource with separate read, write, and watch capabilities.</summary>
    public JsonSectionResource(
        IResourceReader reader,
        IResourceWriter? writer,
        string sectionPath,
        IStateWatcher? watcher = null,
        JsonSerializerOptions? serializerOptions = null,
        ResourceId? resourceId = null
    )
        : this(
            reader,
            writer,
            ParseSectionPath(sectionPath),
            watcher,
            serializerOptions,
            resourceId,
            []
        ) { }

    internal JsonSectionResource(
        IResourceReader reader,
        IResourceWriter? writer,
        string sectionPath,
        IStateWatcher? watcher,
        JsonSerializerOptions? serializerOptions,
        ResourceId? resourceId,
        byte[] schemaShape
    )
        : this(
            reader,
            writer,
            ParseSectionPath(sectionPath),
            watcher,
            serializerOptions,
            resourceId,
            schemaShape
        ) { }

    private JsonSectionResource(
        IResourceReader reader,
        IResourceWriter? writer,
        string[] path,
        IStateWatcher? watcher,
        JsonSerializerOptions? serializerOptions,
        ResourceId? resourceId,
        byte[] schemaShape
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        _reader = reader;
        _writer = writer;
        _watcher = watcher;
        _path = path;
        _schemaShape = schemaShape;
        _serializerOptions = serializerOptions is null
            ? new JsonSerializerOptions { WriteIndented = true }
            : new JsonSerializerOptions(serializerOptions);
        ResourceId =
            resourceId
            ?? (writer as IResourceIdentity ?? reader as IResourceIdentity)?.ResourceId
            ?? new ResourceId($"section:{Guid.NewGuid():N}");
        _batchScope = "json/" + string.Join("/", _path.Select(Uri.EscapeDataString));
    }

    internal static JsonSectionResource CreateRoot(
        IResourceReader reader,
        IResourceWriter? writer,
        IStateWatcher? watcher,
        JsonSerializerOptions? serializerOptions,
        ResourceId? resourceId,
        byte[] schemaShape
    ) => new(reader, writer, [], watcher, serializerOptions, resourceId, schemaShape);

    private static string[] ParseSectionPath(string sectionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);
        var path = sectionPath
            .Replace("__", ":", StringComparison.Ordinal)
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (path.Length == 0)
        {
            throw new ArgumentException(
                "The section path must contain at least one property name.",
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
    public bool AutomaticBackupRecoveryEnabled =>
        _reader is IResourceBackupRecovery recovery && recovery.AutomaticBackupRecoveryEnabled;

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        CancellationToken cancellationToken = default
    )
    {
        var resource = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ExtractSection(resource);
        }
        catch (JsonException exception)
        {
            exception.Data["Configlue.ObservedResourceRevision"] = resource.Revision;
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult?> TryRecoverLatestBackupAsync(
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
                expectedRevision,
                expectedMissing,
                async (candidate, token) =>
                {
                    ResourceReadResult section;
                    try
                    {
                        section = ExtractSection(candidate);
                    }
                    catch (JsonException)
                    {
                        return false;
                    }

                    return section.Status == StateReadStatus.Success
                        && await validate(section, token).ConfigureAwait(false);
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return restored is { } result ? ExtractSection(result) : null;
    }

    private ResourceReadResult ExtractSection(ResourceReadResult resource)
    {
        if (resource.Status != StateReadStatus.Success)
        {
            return new ResourceReadResult(resource.Status, default, resource.Revision);
        }

        if (_path.Length == 0)
        {
            _ = JsoncSyntaxTree.Parse(resource.Content.ToArray());
            return resource;
        }

        var document = JsoncSyntaxTree.Parse(resource.Content.ToArray());
        var current = document.Root;
        foreach (var name in _path)
        {
            if (current.Kind != JsonValueKind.Object)
            {
                throw new JsonException(
                    $"Section path '{string.Join(':', _path)}' crosses a non-object value at '{name}'."
                );
            }

            var property = current.Properties!.SingleOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.Ordinal)
            );
            if (property is null)
            {
                return ResourceReadResult.NotFound(resource.Revision);
            }

            current = property.Value;
        }

        return ResourceReadResult.Success(
            document.GetRawText(current).ToArray(),
            resource.Revision
        );
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var writer =
            _writer ?? throw new NotSupportedException("This JSON section resource is read-only.");
        var current = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var updatedDocument = CreateMutation(request).Apply(current);
        var expectedRevision = request.ExpectedRevision ?? current.Revision;
        var checkRevision =
            request.CheckRevision
            || request.ExpectedRevision is not null
            || current.Revision is not null;
        return await writer
            .WriteAsync(
                new ResourceWriteRequest(
                    updatedDocument,
                    expectedRevision,
                    request.Schema,
                    checkRevision
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ResourceWriteMutation CreateMutation(ResourceWriteRequest request)
    {
        var content = request.Content.ToArray();
        return new ResourceWriteMutation(
            request.ExpectedRevision,
            request.CheckRevision,
            request.Schema,
            current => ApplyToResource(current, content),
            _batchScope,
            canCompose: true
        );
    }

    private ReadOnlyMemory<byte> ApplyToResource(
        ResourceReadResult current,
        ReadOnlyMemory<byte> sectionContent
    )
    {
        if (current.Status is not (StateReadStatus.Success or StateReadStatus.NotFound))
        {
            throw new IOException("The JSON resource is unavailable and cannot be updated safely.");
        }

        return JsoncDocumentEditor.Update(
            current.Status == StateReadStatus.Success ? current.Content : "{}"u8.ToArray(),
            sectionContent,
            _path,
            _schemaShape,
            _serializerOptions
        );
    }

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        if (_watcher is not null)
        {
            await _watcher
                .WaitForChangeAsync(observedRevision, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current.Revision, observedRevision, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
