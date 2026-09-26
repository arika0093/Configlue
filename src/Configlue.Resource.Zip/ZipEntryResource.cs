using System.IO.Compression;

namespace Configlue.Resource.Zip;

/// <summary>A logical resource view over one entry in a shared ZIP archive resource.</summary>
public sealed class ZipEntryResource
    : IResourceReader,
        IResourceWriter,
        IStateWatcher,
        IResourceIdentity,
        IResourceBatchParticipant
{
    private readonly IResourceReader _archiveReader;
    private readonly IResourceBatchWriter? _archiveWriter;
    private readonly IStateWatcher? _archiveWatcher;
    private readonly string _entryName;
    private readonly ResourceId _resourceId;

    /// <summary>Creates an entry view, detecting batch writing and change watching on the archive resource.</summary>
    public ZipEntryResource(
        IResourceReader archiveReader,
        string entryName,
        IStateWatcher? archiveWatcher = null,
        ResourceId? resourceId = null
    )
        : this(
            archiveReader,
            archiveReader as IResourceBatchWriter,
            entryName,
            archiveWatcher,
            resourceId
        ) { }

    /// <summary>Creates an entry view with separate reader and optional batch writer capabilities.</summary>
    public ZipEntryResource(
        IResourceReader archiveReader,
        IResourceBatchWriter? archiveWriter,
        string entryName,
        IStateWatcher? archiveWatcher = null,
        ResourceId? resourceId = null
    )
    {
        ArgumentNullException.ThrowIfNull(archiveReader);
        _archiveReader = archiveReader;
        _archiveWriter = archiveWriter;
        _archiveWatcher =
            archiveWatcher ?? archiveReader as IStateWatcher ?? archiveWriter as IStateWatcher;
        _entryName = NormalizeEntryName(entryName);
        _resourceId =
            resourceId
            ?? archiveWriter?.ResourceId
            ?? (archiveReader as IResourceIdentity)?.ResourceId
            ?? new ResourceId($"zip:{Guid.NewGuid():N}");
    }

    /// <inheritdoc />
    public ResourceId ResourceId => _resourceId;

    /// <inheritdoc />
    public IResourceBatchWriter? BatchWriter => _archiveWriter;

    /// <summary>The normalized entry path inside the archive.</summary>
    public string EntryName => _entryName;

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        CancellationToken cancellationToken = default
    )
    {
        var archiveResult = await _archiveReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (archiveResult.Status != StateReadStatus.Success)
        {
            return archiveResult.Status == StateReadStatus.NotFound
                ? ResourceReadResult.NotFound(archiveResult.Revision)
                : ResourceReadResult.Unavailable(archiveResult.Revision);
        }

        using var content = new MemoryStream(archiveResult.Content.ToArray(), writable: false);
        using var archive = new ZipArchive(content, ZipArchiveMode.Read);
        var entry = archive.GetEntry(_entryName);
        if (entry is null)
        {
            return ResourceReadResult.NotFound(archiveResult.Revision);
        }

        await using var entryStream = await entry
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        using var destination = new MemoryStream();
        await entryStream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        return ResourceReadResult.Success(destination.ToArray(), archiveResult.Revision);
    }

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    ) =>
        (
            _archiveWriter
            ?? throw new NotSupportedException("The ZIP archive resource is read-only.")
        ).WriteBatchAsync([CreateMutation(request)], cancellationToken);

    /// <inheritdoc />
    public ResourceWriteMutation CreateMutation(ResourceWriteRequest request)
    {
        var content = request.Content.ToArray();
        return new ResourceWriteMutation(
            request.ExpectedRevision,
            request.CheckRevision,
            request.Schema,
            current => ReplaceEntry(current, _entryName, content),
            scope: "zip/" + _entryName,
            canCompose: true
        );
    }

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        if (_archiveWatcher is not null)
        {
            await _archiveWatcher
                .WaitForChangeAsync(observedRevision, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await _archiveReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current.Revision, observedRevision, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static ReadOnlyMemory<byte> ReplaceEntry(
        ResourceReadResult current,
        string entryName,
        byte[] content
    )
    {
        if (current.Status == StateReadStatus.Unavailable)
        {
            throw new IOException("The ZIP archive resource is unavailable for writing.");
        }

        using var archiveContent = new MemoryStream();
        if (current.Status == StateReadStatus.Success)
        {
            archiveContent.Write(current.Content.Span);
            archiveContent.Position = 0;
        }

        var mode =
            current.Status == StateReadStatus.Success
                ? ZipArchiveMode.Update
                : ZipArchiveMode.Create;
        using (var archive = new ZipArchive(archiveContent, mode, leaveOpen: true))
        {
            if (mode == ZipArchiveMode.Update)
            {
                foreach (
                    var existingEntry in archive
                        .Entries.Where(entry =>
                            string.Equals(entry.FullName, entryName, StringComparison.Ordinal)
                        )
                        .ToArray()
                )
                {
                    existingEntry.Delete();
                }
            }

            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            entryStream.Write(content);
        }

        return archiveContent.ToArray();
    }

    private static string NormalizeEntryName(string entryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
        var normalized = entryName.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.None);
        if (
            normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Contains('\0')
            || (normalized.Length > 1 && normalized[1] == ':')
            || segments.Any(static segment => segment.Length == 0 || segment is "." or "..")
        )
        {
            throw new ArgumentException(
                "ZIP entry names must be relative paths without empty, '.' or '..' segments.",
                nameof(entryName)
            );
        }

        return normalized;
    }
}
