using System.Security.Cryptography;
using Configlue.Resource.Zip;

namespace Configlue.Source.Presets;

internal sealed class SingleBinaryEntryResource
    : IResourceReader,
        IPipelineResourceReader,
        IResourceWriter,
        IStateWatcher
{
    private const int RevisionMapLimit = 8;
    private const string MissingEntryRevision = "missing";
    private readonly ZipEntryResource _entry;
    private readonly object _revisionGate = new();
    private readonly Dictionary<string, string?> _archiveRevisions = new(StringComparer.Ordinal);
    private readonly Queue<string> _revisionOrder = new();
    private readonly Dictionary<string, string> _entryRevisionsByArchiveRevision = new(
        StringComparer.Ordinal
    );
    private readonly Queue<string> _archiveRevisionOrder = new();

    public SingleBinaryEntryResource(ZipEntryResource entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entry = entry;
    }

    public ResourceId ResourceId => _entry.ResourceId;

    public bool IsPipelineReadPreferred => false;

    public async ValueTask<ResourceReadResult> ReadAsync(
        CancellationToken cancellationToken = default
    )
    {
        var result = await _entry.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (result.Status == StateReadStatus.Unavailable)
        {
            return result;
        }

        var entryRevision =
            result.Status == StateReadStatus.NotFound
                ? MissingEntryRevision
                : GetEntryRevision(result.Content.Span);
        var exposedRevision = entryRevision;
        StoreArchiveRevision(entryRevision, result.Revision);
        StoreEntryRevision(result.Revision, entryRevision);
        return result with { Revision = exposedRevision };
    }

    public async ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        CancellationToken cancellationToken = default
    )
    {
        var result = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return await PipelineResourceReader
            .FromMemoryAsync(result, cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<StateWriteResult> WriteAsync(
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var archiveRevision = ResolveArchiveRevision(request.ExpectedRevision);
        return WriteEntryAsync(
            new ResourceWriteRequest(
                request.Content,
                archiveRevision,
                request.Schema,
                request.CheckRevision
            ),
            request.Content,
            cancellationToken
        );
    }

    public ValueTask WaitForChangeAsync(
        string? observedRevision,
        CancellationToken cancellationToken = default
    ) => _entry.WaitForChangeAsync(ResolveArchiveRevision(observedRevision), cancellationToken);

    private void StoreArchiveRevision(string entryRevision, string? archiveRevision)
    {
        lock (_revisionGate)
        {
            if (_archiveRevisions.ContainsKey(entryRevision))
            {
                _archiveRevisions[entryRevision] = archiveRevision;
                return;
            }

            _archiveRevisions.Add(entryRevision, archiveRevision);
            _revisionOrder.Enqueue(entryRevision);
            while (_revisionOrder.Count > RevisionMapLimit)
            {
                _archiveRevisions.Remove(_revisionOrder.Dequeue());
            }
        }
    }

    private void StoreEntryRevision(string? archiveRevision, string entryRevision)
    {
        var key = archiveRevision ?? string.Empty;
        lock (_revisionGate)
        {
            if (_entryRevisionsByArchiveRevision.ContainsKey(key))
            {
                _entryRevisionsByArchiveRevision[key] = entryRevision;
                return;
            }

            _entryRevisionsByArchiveRevision.Add(key, entryRevision);
            _archiveRevisionOrder.Enqueue(key);
            while (_archiveRevisionOrder.Count > RevisionMapLimit)
            {
                _entryRevisionsByArchiveRevision.Remove(_archiveRevisionOrder.Dequeue());
            }
        }
    }

    private bool HasUnchangedEntryAtRevision(string? archiveRevision, string currentEntryRevision)
    {
        if (archiveRevision is null)
        {
            return false;
        }

        lock (_revisionGate)
        {
            return _entryRevisionsByArchiveRevision.TryGetValue(archiveRevision, out var baseline)
                && string.Equals(baseline, currentEntryRevision, StringComparison.Ordinal);
        }
    }

    private string? ResolveArchiveRevision(string? entryRevision)
    {
        if (entryRevision is null)
        {
            return null;
        }

        lock (_revisionGate)
        {
            return _archiveRevisions.TryGetValue(entryRevision, out var archiveRevision)
                ? archiveRevision
                : entryRevision;
        }
    }

    private async ValueTask<StateWriteResult> WriteEntryAsync(
        ResourceWriteRequest request,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken
    )
    {
        var current = await _entry.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (current.Status == StateReadStatus.Unavailable)
        {
            throw new IOException("The ZIP entry is unavailable for writing.");
        }

        var currentRevision =
            current.Status == StateReadStatus.NotFound
                ? MissingEntryRevision
                : GetEntryRevision(current.Content.Span);
        var expectedRevision = request.ExpectedRevision ?? MissingEntryRevision;
        var expectedRevisionMatches =
            string.Equals(expectedRevision, currentRevision, StringComparison.Ordinal)
            || string.Equals(expectedRevision, current.Revision, StringComparison.Ordinal)
            || HasUnchangedEntryAtRevision(request.ExpectedRevision, currentRevision);
        if (request.CheckRevision && !expectedRevisionMatches)
        {
            throw new StateConflictException(
                $"The ZIP entry '{_entry.EntryName}' changed after the configuration state was read."
            );
        }

        await _entry
            .WriteAsync(
                new ResourceWriteRequest(
                    content,
                    current.Revision,
                    request.Schema,
                    CheckRevision: true
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        var written = await _entry.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (written.Status != StateReadStatus.Success)
        {
            throw new IOException(
                "The ZIP entry was unavailable after a successful archive write."
            );
        }

        var writtenRevision = GetEntryRevision(written.Content.Span);
        if (
            !string.Equals(
                writtenRevision,
                GetEntryRevision(content.Span),
                StringComparison.Ordinal
            )
        )
        {
            throw new StateConflictException(
                "The ZIP entry changed before the configuration write completed."
            );
        }

        StoreArchiveRevision(writtenRevision, written.Revision);
        return new StateWriteResult(writtenRevision);
    }

    private static string GetEntryRevision(ReadOnlySpan<byte> content) =>
        "entry:" + Convert.ToHexString(SHA256.HashData(content));
}
