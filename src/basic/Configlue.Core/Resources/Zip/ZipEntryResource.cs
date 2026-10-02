using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Configlue.Sources;

namespace Configlue.Resource.Zip;

/// <summary>A logical resource view over one entry in a shared ZIP archive resource.</summary>
public sealed class ZipEntryResource
    : IResourceReader,
        IPipelineResourceReader,
        IResourceWriter,
        ISourceWatcher,
        ITryResourceIdentity,
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
    private readonly ISourceWatcher? _archiveWatcher;
    private readonly string _entryName;
    private readonly Func<ConfiglueResourceContext, string>? _entryNameSelector;
    private readonly ResourceId? _configuredResourceId;
    private readonly object _snapshotGate = new();
    private readonly Dictionary<
        (ResourceKey Key, RouteKey Route, string Revision),
        string
    > _readSnapshots = [];
    private readonly Queue<(ResourceKey Key, RouteKey Route, string Revision)> _snapshotOrder =
        new();
    private readonly TimeSpan _pollingInterval;

    /// <summary>Creates an entry view, detecting batch writing and change watching on the archive resource.</summary>
    public ZipEntryResource(
        IResourceReader archiveReader,
        string entryName,
        ISourceWatcher? archiveWatcher = null,
        ResourceId? fixedResourceId = null
    )
        : this(
            archiveReader,
            archiveReader as IResourceBatchWriter,
            entryName,
            archiveWatcher,
            fixedResourceId,
            new PollingOptions(TimeSpan.FromMilliseconds(250))
        ) { }

    /// <summary>Creates an entry view with optional subject-aware entry selection.</summary>
    public ZipEntryResource(
        IResourceReader archiveReader,
        ZipEntryResourceOptions options,
        string entryName,
        ISourceWatcher? archiveWatcher = null,
        ResourceId? fixedResourceId = null
    )
        : this(
            archiveReader,
            archiveReader as IResourceBatchWriter,
            entryName,
            archiveWatcher,
            fixedResourceId,
            new PollingOptions(TimeSpan.FromMilliseconds(250)),
            options
        ) { }

    /// <summary>Creates an entry view with a configured fallback polling interval.</summary>
    public ZipEntryResource(
        IResourceReader archiveReader,
        string entryName,
        TimeSpan pollingInterval,
        ISourceWatcher? archiveWatcher = null,
        ResourceId? fixedResourceId = null
    )
        : this(
            archiveReader,
            archiveReader as IResourceBatchWriter,
            entryName,
            archiveWatcher,
            fixedResourceId,
            new PollingOptions(pollingInterval)
        ) { }

    /// <summary>Creates an entry view with separate reader and optional batch writer capabilities.</summary>
    public ZipEntryResource(
        IResourceReader archiveReader,
        IResourceBatchWriter? archiveWriter,
        string entryName,
        ISourceWatcher? archiveWatcher = null,
        ResourceId? fixedResourceId = null
    )
        : this(
            archiveReader,
            archiveWriter,
            entryName,
            archiveWatcher,
            fixedResourceId,
            new PollingOptions(TimeSpan.FromMilliseconds(250))
        ) { }

    /// <summary>Creates an entry view with separate archive capabilities and subject-aware entry selection.</summary>
    public ZipEntryResource(
        IResourceReader archiveReader,
        IResourceBatchWriter? archiveWriter,
        ZipEntryResourceOptions options,
        string entryName,
        ISourceWatcher? archiveWatcher = null,
        ResourceId? fixedResourceId = null
    )
        : this(
            archiveReader,
            archiveWriter,
            entryName,
            archiveWatcher,
            fixedResourceId,
            new PollingOptions(TimeSpan.FromMilliseconds(250)),
            options
        ) { }

    /// <summary>Creates an entry view with separate capabilities and a configured fallback polling interval.</summary>
    public ZipEntryResource(
        IResourceReader archiveReader,
        IResourceBatchWriter? archiveWriter,
        string entryName,
        TimeSpan pollingInterval,
        ISourceWatcher? archiveWatcher = null,
        ResourceId? fixedResourceId = null
    )
        : this(
            archiveReader,
            archiveWriter,
            entryName,
            archiveWatcher,
            fixedResourceId,
            new PollingOptions(pollingInterval)
        ) { }

    private ZipEntryResource(
        IResourceReader archiveReader,
        IResourceBatchWriter? archiveWriter,
        string entryName,
        ISourceWatcher? archiveWatcher,
        ResourceId? fixedResourceId,
        PollingOptions pollingOptions,
        ZipEntryResourceOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(archiveReader);
        _archiveReader = archiveReader;
        _archiveWriter = archiveWriter;
        _archiveWatcher =
            archiveWatcher ?? archiveReader as ISourceWatcher ?? archiveWriter as ISourceWatcher;
        _entryName = NormalizeEntryName(entryName);
        _entryNameSelector = options?.EntryNameSelector;
        _configuredResourceId = fixedResourceId;
        _pollingInterval = pollingOptions.Interval;
    }

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        TryGetResourceId(context, out var fixedResourceId)
            ? fixedResourceId
            : throw new InvalidOperationException("The archive resource has no physical identity.");

    /// <inheritdoc />
    public bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId resourceId)
    {
        if (_configuredResourceId is { } configuredResourceId)
        {
            resourceId = configuredResourceId;
            return true;
        }

        if (((object?)_archiveWriter).TryGetResourceId(context, out resourceId))
        {
            return true;
        }

        return _archiveReader.TryGetResourceId(context, out resourceId);
    }

    /// <inheritdoc />
    public IResourceBatchWriter? BatchWriter => _archiveWriter;

    /// <summary>The normalized entry path inside the archive.</summary>
    public string EntryName => _entryName;

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
                StoreSnapshot(context, archiveResult.Revision, MissingEntryFingerprint);
                return ResourceReadResult.NotFound(archiveResult.Revision);
            }

            return ResourceReadResult.Unavailable(archiveResult.Revision);
        }

        using var content = CreateReadOnlyStream(archiveResult.Content);
        using var archive = new ZipArchive(content, ZipArchiveMode.Read);
        var entryName = ResolveEntryName(context);
        var entry = archive.GetEntry(entryName);
        if (entry is null)
        {
            StoreSnapshot(context, archiveResult.Revision, MissingEntryFingerprint);
            return ResourceReadResult.NotFound(archiveResult.Revision);
        }

#if NETSTANDARD
        using var entryStream = entry.Open();
#else
        await using var entryStream = await entry
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);
#endif
        var (entryContent, entryRevision) = await ReadEntryAsync(
                entryStream,
                entry.Length,
                cancellationToken
            )
            .ConfigureAwait(false);
        StoreSnapshot(context, archiveResult.Revision, entryRevision);
        return ResourceReadResult.Success(entryContent, archiveResult.Revision);
    }

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
    public ResourceWriteMutation CreateMutation(
        ConfiglueResourceContext context,
        ResourceWriteRequest request
    )
    {
        var entryName = ResolveEntryName(context);
        var content = request.Content.ToArray();
        var expectedSnapshot = string.Empty;
        var hasSnapshot =
            request.Condition.IsMatch
            && TryGetSnapshot(context, request.Condition.Revision, out expectedSnapshot);
        return new ResourceWriteMutation(
            hasSnapshot || request.Condition.IsMustNotExist
                ? RevisionCondition.None
                : request.Condition,
            request.Schema,
            current =>
            {
                if (
                    request.Condition.IsMustNotExist
                    && GetCurrentEntryFingerprint(current, entryName) != MissingEntryFingerprint
                )
                {
                    throw new StateConflictException(
                        $"The ZIP entry '{entryName}' already exists."
                    );
                }
                if (
                    hasSnapshot
                    && !string.Equals(
                        expectedSnapshot,
                        GetCurrentEntryFingerprint(current, entryName),
                        StringComparison.Ordinal
                    )
                )
                {
                    throw new StateConflictException(
                        $"The ZIP entry '{entryName}' changed after it was read."
                    );
                }

                return ReplaceEntry(current, entryName, content);
            },
            scope: "zip/" + entryName,
            canCompose: true,
            context: context
        );
    }

    private string ResolveEntryName(ConfiglueResourceContext context) =>
        _entryNameSelector is null ? _entryName : NormalizeEntryName(_entryNameSelector(context));

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
            var currentContent = current.Content.ToArray();
            archiveContent.Write(currentContent, 0, currentContent.Length);
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
            entryStream.Write(content, 0, content.Length);
        }

        return archiveContent.ToArray();
    }

    private void StoreSnapshot(
        ConfiglueResourceContext context,
        string? archiveRevision,
        string entryFingerprint
    )
    {
        var key = (context.ResourceKey, context.Route, archiveRevision ?? string.Empty);
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

    private bool TryGetSnapshot(
        ConfiglueResourceContext context,
        string? revision,
        out string fingerprint
    )
    {
        var key = (context.ResourceKey, context.Route, revision ?? string.Empty);
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
            await entryStream
                .CopyToAsync(destination, 81920, cancellationToken)
                .ConfigureAwait(false);
            var fallback = destination.ToArray();
            return (fallback, GetEntryFingerprint(fallback));
        }

        var content = new byte[(int)length];
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var offset = 0;
        while (offset < content.Length)
        {
#if NETSTANDARD
            var read = await entryStream
                .ReadAsync(content, offset, content.Length - offset, cancellationToken)
                .ConfigureAwait(false);
#else
            var read = await entryStream
                .ReadAsync(content.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
#endif
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
        var segments = normalized.Split(new[] { '/' }, StringSplitOptions.None);
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
