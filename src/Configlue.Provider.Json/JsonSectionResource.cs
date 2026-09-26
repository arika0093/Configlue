using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Configlue.Provider.Json;

/// <summary>Exposes a nested JSON object as an independently revisioned resource view.</summary>
public sealed class JsonSectionResource
    : IResourceReader,
        IResourceWriter,
        IStateWatcher,
        IResourceIdentity,
        IResourceBatchParticipant
{
    private readonly IResourceReader _reader;
    private readonly IResourceWriter? _writer;
    private readonly IStateWatcher? _watcher;
    private readonly string[] _path;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly string _batchScope;

    /// <summary>Creates a section resource over an existing JSON resource.</summary>
    /// <param name="resource">The physical resource containing the JSON document.</param>
    /// <param name="sectionPath">A colon- or double-underscore-separated path to the section.</param>
    /// <param name="serializerOptions">Options used to format the updated document.</param>
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
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);

        _reader = reader;
        _writer = writer;
        _watcher = watcher;
        ResourceId =
            resourceId
            ?? (writer as IResourceIdentity ?? reader as IResourceIdentity)?.ResourceId
            ?? new ResourceId($"section:{Guid.NewGuid():N}");
        _path = sectionPath
            .Replace("__", ":", StringComparison.Ordinal)
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (_path.Length == 0)
        {
            throw new ArgumentException(
                "The section path must contain at least one property name.",
                nameof(sectionPath)
            );
        }

        _batchScope = "json/" + string.Join("/", _path.Select(Uri.EscapeDataString));

        _serializerOptions = serializerOptions is null
            ? new JsonSerializerOptions { WriteIndented = true }
            : new JsonSerializerOptions(serializerOptions);
    }

    /// <summary>Whether a physical writer was supplied.</summary>
    public bool CanWrite => _writer is not null;

    /// <inheritdoc />
    public ResourceId ResourceId { get; }

    /// <inheritdoc />
    public IResourceBatchWriter? BatchWriter => _writer as IResourceBatchWriter;

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        CancellationToken cancellationToken = default
    )
    {
        var resource = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (resource.Status != StateReadStatus.Success)
        {
            return new ResourceReadResult(resource.Status, default, resource.Revision);
        }

        using var document = JsonDocument.Parse(resource.Content);
        var current = document.RootElement;
        foreach (var name in _path)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException(
                    $"Section path '{string.Join(':', _path)}' crosses a non-object value at '{name}'."
                );
            }

            if (!current.TryGetProperty(name, out current))
            {
                return ResourceReadResult.NotFound(resource.Revision);
            }
        }

        return ResourceReadResult.Success(
            Encoding.UTF8.GetBytes(current.GetRawText()),
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
        JsonObject root;
        if (current.Status == StateReadStatus.Success)
        {
            var parsedRoot = JsonNode.Parse(current.Content.Span);
            root =
                parsedRoot as JsonObject
                ?? throw new JsonException(
                    "A JSON section resource must be contained in a root object."
                );
        }
        else if (current.Status == StateReadStatus.NotFound)
        {
            root = new JsonObject();
        }
        else
        {
            throw new IOException("The JSON resource is unavailable and cannot be updated safely.");
        }

        var container = root;
        for (var index = 0; index < _path.Length - 1; index++)
        {
            var name = _path[index];
            if (!container.TryGetPropertyValue(name, out var child))
            {
                child = new JsonObject();
                container[name] = child;
            }
            else if (child is not JsonObject)
            {
                throw new JsonException(
                    $"Section path '{string.Join(':', _path)}' crosses a non-object value at '{name}'."
                );
            }

            container = (JsonObject)child;
        }

        container[_path[^1]] = JsonNode.Parse(sectionContent.Span);
        return Encoding.UTF8.GetBytes(root.ToJsonString(_serializerOptions));
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
