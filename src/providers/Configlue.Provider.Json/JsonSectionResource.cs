using System.Text.Json;
using Configlue.Sources;

namespace Configlue.Provider.Json;

/// <summary>Exposes a nested JSON object as an independently revisioned resource view.</summary>
/// <remarks>
/// A section is a logical view over one physical JSON document shared with sibling sections. The
/// logical schema supplied when writing a section belongs to the section payload and is never
/// written to the physical document's schema metadata. To declare the physical container schema,
/// set <see cref="ContainerSchema"/> explicitly; otherwise section writes leave the container's
/// existing schema metadata unchanged. Whole-document (root) views forward the logical schema
/// because for them the logical and physical schema coincide.
/// </remarks>
public sealed class JsonSectionResource
    : IResourceReader,
        IPipelineResourceReader,
        IResourceWriter,
        ISourceWatcher,
        ITryResourceIdentity,
        IResourceBatchParticipant,
        IContextualResourceBackupRecovery
{
    private readonly IResourceReader _reader;
    private readonly IResourceWriter? _writer;
    private readonly ISourceWatcher? _watcher;
    private readonly string[] _path;
    private readonly string _batchScope;
    private readonly ResourceId? _configuredResourceId;
    private readonly byte[] _schemaShape;
    private readonly JsonSerializerOptions _serializerOptions;

    /// <summary>Creates a section resource over an existing JSON resource.</summary>
    /// <param name="resource">The physical resource containing the JSON document.</param>
    /// <param name="sectionPath">A colon- or double-underscore-separated path to the section.</param>
    /// <param name="serializerOptions">Options used to format newly created JSON structure.</param>
    /// <param name="fixedResourceId">An optional stable identity for the physical resource.</param>
    public JsonSectionResource(
        IResourceReader resource,
        string sectionPath,
        JsonSerializerOptions? serializerOptions = null,
        ResourceId? fixedResourceId = null
    )
        : this(
            resource,
            resource as IResourceWriter,
            sectionPath,
            resource as ISourceWatcher,
            serializerOptions,
            fixedResourceId
        ) { }

    /// <summary>Creates a section resource with separate read, write, and watch capabilities.</summary>
    public JsonSectionResource(
        IResourceReader reader,
        IResourceWriter? writer,
        string sectionPath,
        ISourceWatcher? watcher = null,
        JsonSerializerOptions? serializerOptions = null,
        ResourceId? fixedResourceId = null
    )
        : this(
            reader,
            writer,
            ParseSectionPath(sectionPath),
            watcher,
            serializerOptions,
            fixedResourceId,
            []
        ) { }

    internal JsonSectionResource(
        IResourceReader reader,
        IResourceWriter? writer,
        string sectionPath,
        ISourceWatcher? watcher,
        JsonSerializerOptions? serializerOptions,
        ResourceId? fixedResourceId,
        byte[] schemaShape
    )
        : this(
            reader,
            writer,
            ParseSectionPath(sectionPath),
            watcher,
            serializerOptions,
            fixedResourceId,
            schemaShape
        ) { }

    private JsonSectionResource(
        IResourceReader reader,
        IResourceWriter? writer,
        string[] path,
        ISourceWatcher? watcher,
        JsonSerializerOptions? serializerOptions,
        ResourceId? fixedResourceId,
        byte[] schemaShape
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        _reader = reader;
        _writer = writer;
        _watcher = watcher;
        _path = path;
        _schemaShape = schemaShape;
        _configuredResourceId = fixedResourceId;
        _serializerOptions = serializerOptions is null
            ? new JsonSerializerOptions { WriteIndented = true }
            : new JsonSerializerOptions(serializerOptions);
        _batchScope = "json/" + string.Join("/", _path.Select(Uri.EscapeDataString));
    }

    internal static JsonSectionResource CreateRoot(
        IResourceReader reader,
        IResourceWriter? writer,
        ISourceWatcher? watcher,
        JsonSerializerOptions? serializerOptions,
        ResourceId? fixedResourceId,
        byte[] schemaShape
    ) => new(reader, writer, [], watcher, serializerOptions, fixedResourceId, schemaShape);

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
                "The section path must contain at least one property name.",
                nameof(sectionPath)
            );
        }

        return path;
    }

    /// <summary>Whether a physical writer was supplied.</summary>
    public bool CanWrite => _writer is not null;

    /// <summary>
    /// The schema metadata of the physical JSON document that hosts this section. Set this only
    /// when the shared container itself is known to use that schema. When <see langword="null"/>
    /// (the default) section writes make no schema claim and preserve existing container metadata.
    /// This property has no effect on whole-document (root) views.
    /// </summary>
    public StateSchemaMetadata? ContainerSchema { get; init; }

    /// <inheritdoc />
    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        TryGetResourceId(context, out var fixedResourceId)
            ? fixedResourceId
            : throw new InvalidOperationException(
                "The underlying resource has no physical identity."
            );

    /// <inheritdoc />
    public bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId resourceId)
    {
        if (_configuredResourceId is { } configuredResourceId)
        {
            resourceId = configuredResourceId;
            return true;
        }

        if (_writer.TryGetResourceId(context, out resourceId))
        {
            return true;
        }

        return _reader.TryGetResourceId(context, out resourceId);
    }

    /// <inheritdoc />
    public IResourceBatchWriter? BatchWriter => _writer as IResourceBatchWriter;

    /// <inheritdoc />
    public bool IsPipelineReadPreferred => false;

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
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var resource = await _reader.ReadAsync(context, cancellationToken).ConfigureAwait(false);
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
            return resource.Status switch
            {
                StateReadStatus.NotFound => ResourceReadResult.NotFound(resource.Revision),
                StateReadStatus.Unavailable => ResourceReadResult.Unavailable(resource.Revision),
                StateReadStatus.InvalidPayload => ResourceReadResult.InvalidPayload(
                    resource.Revision
                ),
                _ => throw new InvalidOperationException("Unexpected non-success resource status."),
            };
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
                    $"Section path '{string.Join(":", _path)}' crosses a non-object value at '{name}'."
                );
            }

            var properties = current.Properties!;
            JsoncPropertyNode? property = null;
            for (var index = 0; index < properties.Count; index++)
            {
                if (string.Equals(properties[index].Name, name, StringComparison.Ordinal))
                {
                    property = properties[index];
                    break;
                }
            }

            if (property is null)
            {
                return ResourceReadResult.NotFound(resource.Revision);
            }

            current = property.Value;
        }

        return ResourceReadResult.Success(document.GetRawText(current), resource.Revision);
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var writer =
            _writer ?? throw new NotSupportedException("This JSON section resource is read-only.");
        var current = await _reader.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        var updatedDocument = CreateMutation(context, request).Apply(current);
        return await writer
            .WriteAsync(
                context,
                new ResourceWriteRequest(
                    updatedDocument,
                    Condition: RevisionCondition.FromRevision(current.Revision),
                    Schema: _path.Length == 0 ? request.Schema : ContainerSchema ?? current.Schema
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ResourceWriteMutation CreateMutation(
        ConfiglueResourceContext context,
        ResourceWriteRequest request
    )
    {
        var content = request.Content.ToArray();
        return new ResourceWriteMutation(
            request.Condition.IsMustNotExist ? RevisionCondition.None : request.Condition,
            ResolvePhysicalSchema(request),
            current =>
            {
                if (!request.Condition.IsNone)
                {
                    var section = ExtractSection(current);
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

    private StateSchemaMetadata? ResolvePhysicalSchema(ResourceWriteRequest request) =>
        _path.Length == 0 ? request.Schema : ContainerSchema;

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
}
