using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Configlue.Resource.Zip;

/// <summary>A logical resource view over one entry in a shared ZIP archive resource.</summary>
public sealed class ZipEntryResource
    : IResourceReader,
        IPipelineResourceReader,
        IResourceWriter,
        IStateWatcher,
        IResourceIdentity,
        IResourceBatchParticipant
{
    private readonly struct PollingOptions
    {
        public TimeSpan Interval { get; }

        public PollingOptions(TimeSpan pollingInterval)
        {
            if (pollingInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pollingInterval),
                    "The polling interval must be greater than zero."
                );
            }

            Interval = pollingInterval;
        }
    }

    private const int ReadSnapshotLimit = 8;
    private const string MissingEntryFingerprint = "missing";
    private const string PresentEntryFingerprintPrefix = "present:";
    private readonly IResourceReader _archiveReader;
    private readonly IResourceBatchWriter? _archiveWriter;
    private readonly IStateWatcher? _archiveWatcher;
    private readonly string _entryName;
    private readonly ResourceId _resourceId;
    private readonly ResourceId? _configuredResourceId;
    private readonly object _snapshotGate = new();
    private readonly Dictionary<(SubjectKey Key, string Revision), string> _readSnapshots = [];
    private readonly Queue<(SubjectKey Key, string Revision)> _snapshotOrder = new();
    private readonly TimeSpan _pollingInterval;

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
            resourceId,
            new PollingOptions(TimeSpan.FromMilliseconds(250))
        ) { }

    /// <summary>Creates an entry view with a configured fallback polling interval.</summary>
    public ZipEntryResource(
        IResourceReader archiveReader,
        string entryName,
        TimeSpan pollingInterval,
        IStateWatcher? archiveWatcher = null,
        ResourceId? resourceId = null
    )
        : this(
            archiveReader,
            archiveReader as IResourceBatchWriter,
            entryName,
            archiveWatcher,
            resourceId,
            new PollingOptions(pollingInterval)
        ) { }

    /// <summary>Creates an entry view with separate reader and optional batch writer capabilities.</summary>
    public ZipEntryResource(
        IResourceReader archiveReader,
        IResourceBatchWriter? archiveWriter,
        string entryName,
        IStateWatcher? archiveWatcher = null,
        ResourceId? resourceId = null
    )
        : this(
            archiveReader,
            archiveWriter,
            entryName,
            archiveWatcher,
            resourceId,
            new PollingOptions(TimeSpan.FromMilliseconds(250))
        ) { }

    /// <summary>Creates an entry view with separate capabilities and a configured fallback polling interval.</summary>
    public ZipEntryResource(
        IResourceReader archiveReader,
        IResourceBatchWriter? archiveWriter,
        string entryName,
        TimeSpan pollingInterval,
        IStateWatcher? archiveWatcher = null,
        ResourceId? resourceId = null
    )
        : this(
            archiveReader,
            archiveWriter,
            entryName,
            archiveWatcher,
            resourceId,
            new PollingOptions(pollingInterval)
        ) { }

    private ZipEntryResource(
        IResourceReader archiveReader,
        IResourceBatchWriter? archiveWriter,
        string entryName,
        IStateWatcher? archiveWatcher,
        ResourceId? resourceId,
        PollingOptions pollingOptions
    )
    {
        ArgumentNullException.ThrowIfNull(archiveReader);
        _archiveReader = archiveReader;
        _archiveWriter = archiveWriter;
        _archiveWatcher =
            archiveWatcher ?? archiveReader as IStateWatcher ?? archiveWriter as IStateWatcher;
        _entryName = NormalizeEntryName(entryName);
        _configuredResourceId = resourceId;
        _resourceId =
            resourceId
            ?? TryGetResourceId(archiveWriter, ConfiglueResourceContext.Default)
            ?? TryGetResourceId(archiveReader, ConfiglueResourceContext.Default)
            ?? new ResourceId($"zip:{Guid.NewGuid():N}");
        _pollingInterval = pollingOptions.Interval;
    }

    /// <inheritdoc />
    public ResourceId ResourceId => _resourceId;

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        TryGetResourceId(context, out var resourceId) ? resourceId : _resourceId;

    /// <inheritdoc />
    public bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId resourceId)
    {
        if (_configuredResourceId is { } configuredResourceId)
        {
            resourceId = configuredResourceId;
            return true;
        }

        if (
            _archiveWriter is IResourceIdentity writerIdentity
            && writerIdentity.TryGetResourceId(context, out resourceId)
        )
        {
            return true;
        }

        if (
            _archiveReader is IResourceIdentity readerIdentity
            && readerIdentity.TryGetResourceId(context, out resourceId)
        )
        {
            return true;
        }

        resourceId = _resourceId;
        return true;
    }

    /// <inheritdoc />
    public IResourceBatchWriter? BatchWriter => _archiveWriter;

    /// <summary>The normalized entry path inside the archive.</summary>
    public string EntryName => _entryName;

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
    public ValueTask<ResourceReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(ConfiglueResourceContext.Default, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var archiveResult = await _archiveReader
            .ReadAsync(context, cancellationToken)
            .ConfigureAwait(false);
        if (archiveResult.Status != StateReadStatus.Success)
        {
            if (archiveResult.Status == StateReadStatus.NotFound)
            {
                StoreSnapshot(context.Key, archiveResult.Revision, MissingEntryFingerprint);
                return ResourceReadResult.NotFound(archiveResult.Revision);
            }

            return ResourceReadResult.Unavailable(archiveResult.Revision);
        }

        using var content = CreateReadOnlyStream(archiveResult.Content);
        using var archive = new ZipArchive(content, ZipArchiveMode.Read);
        var entry = archive.GetEntry(_entryName);
        if (entry is null)
        {
            StoreSnapshot(context.Key, archiveResult.Revision, MissingEntryFingerprint);
            return ResourceReadResult.NotFound(archiveResult.Revision);
        }

        await using var entryStream = await entry
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);
        var (entryContent, entryRevision) = await ReadEntryAsync(
                entryStream,
                entry.Length,
                cancellationToken
            )
            .ConfigureAwait(false);
        StoreSnapshot(context.Key, archiveResult.Revision, entryRevision);
        return ResourceReadResult.Success(entryContent, archiveResult.Revision);
    }

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    ) => WriteAsync(ConfiglueResourceContext.Default, request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    ) =>
        (
            _archiveWriter
            ?? throw new NotSupportedException("The ZIP archive resource is read-only.")
        ).WriteBatchAsync([CreateMutation(context, request)], cancellationToken);

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
        var expectedSnapshot = string.Empty;
        var hasSnapshot =
            request.Condition.IsMatch
            && TryGetSnapshot(context.Key, request.Condition.Revision, out expectedSnapshot);
        return new ResourceWriteMutation(
            hasSnapshot || request.Condition.IsMustNotExist
                ? RevisionCondition.None
                : request.Condition,
            request.Schema,
            current =>
            {
                if (
                    request.Condition.IsMustNotExist
                    && GetCurrentEntryFingerprint(current, _entryName) != MissingEntryFingerprint
                )
                {
                    throw new StateConflictException(
                        $"The ZIP entry '{_entryName}' already exists."
                    );
                }
                if (
                    hasSnapshot
                    && !string.Equals(
                        expectedSnapshot,
                        GetCurrentEntryFingerprint(current, _entryName),
                        StringComparison.Ordinal
                    )
                )
                {
                    throw new StateConflictException(
                        $"The ZIP entry '{_entryName}' changed after it was read."
                    );
                }

                return ReplaceEntry(current, _entryName, content);
            },
            scope: "zip/" + _entryName,
            canCompose: true,
            context: context
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
        if (_archiveWatcher is not null)
        {
            await _archiveWatcher
                .WaitForChangeAsync(context, observedRevision, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await _archiveReader
                .ReadAsync(context, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(current.Revision, observedRevision, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(_pollingInterval, cancellationToken).ConfigureAwait(false);
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

    private void StoreSnapshot(
        SubjectKey subjectKey,
        string? archiveRevision,
        string entryFingerprint
    )
    {
        var key = (subjectKey, archiveRevision ?? string.Empty);
        lock (_snapshotGate)
        {
            if (_readSnapshots.ContainsKey(key))
            {
                _readSnapshots[key] = entryFingerprint;
                return;
            }

            _readSnapshots.Add(key, entryFingerprint);
            _snapshotOrder.Enqueue(key);
            while (_snapshotOrder.Count > ReadSnapshotLimit)
            {
                _readSnapshots.Remove(_snapshotOrder.Dequeue());
            }
        }
    }

    private static ResourceId? TryGetResourceId(
        object? resource,
        ConfiglueResourceContext context
    ) =>
        resource is IResourceIdentity identity && identity.TryGetResourceId(context, out var id)
            ? id
            : null;

    private bool TryGetSnapshot(SubjectKey subjectKey, string? revision, out string fingerprint)
    {
        var key = (subjectKey, revision ?? string.Empty);
        lock (_snapshotGate)
        {
            return _readSnapshots.TryGetValue(key, out fingerprint!);
        }
    }

    private static string GetCurrentEntryFingerprint(ResourceReadResult current, string entryName)
    {
        if (current.Status == StateReadStatus.Unavailable)
        {
            throw new IOException("The ZIP archive resource is unavailable for writing.");
        }
        if (current.Status == StateReadStatus.NotFound)
        {
            return MissingEntryFingerprint;
        }

        using var content = new MemoryStream(current.Content.ToArray(), writable: false);
        using var archive = new ZipArchive(content, ZipArchiveMode.Read);
        var entry = archive.GetEntry(entryName);
        if (entry is null)
        {
            return MissingEntryFingerprint;
        }

        using var entryStream = entry.Open();
        using var destination = new MemoryStream();
        entryStream.CopyTo(destination);
        return GetEntryFingerprint(
            destination.GetBuffer().AsSpan(0, checked((int)destination.Length))
        );
    }

    private static string GetEntryFingerprint(ReadOnlySpan<byte> content) =>
        PresentEntryFingerprintPrefix + Convert.ToHexString(SHA256.HashData(content));

    private static async ValueTask<(byte[] Content, string Fingerprint)> ReadEntryAsync(
        Stream entryStream,
        long length,
        CancellationToken cancellationToken
    )
    {
        if (length is < 0 or > int.MaxValue)
        {
            using var destination = new MemoryStream();
            await entryStream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            var fallback = destination.ToArray();
            return (fallback, GetEntryFingerprint(fallback));
        }

        var content = new byte[(int)length];
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var offset = 0;
        while (offset < content.Length)
        {
            var read = await entryStream
                .ReadAsync(content.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hasher.AppendData(content, offset, read);
            offset += read;
        }

        if (offset != content.Length)
        {
            Array.Resize(ref content, offset);
        }

        return (
            content,
            PresentEntryFingerprintPrefix + Convert.ToHexString(hasher.GetHashAndReset())
        );
    }

    private static MemoryStream CreateReadOnlyStream(ReadOnlyMemory<byte> content)
    {
        if (MemoryMarshal.TryGetArray(content, out var segment) && segment.Array is not null)
        {
            return new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false);
        }

        return new MemoryStream(content.ToArray(), writable: false);
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
