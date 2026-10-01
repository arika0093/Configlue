using System.Security.Cryptography;
using Configlue.Resource.Zip;
using Configlue.Sources;

namespace Configlue.Source.Presets;

internal sealed class SingleBinaryEntryResource
    : IResourceReader,
        IPipelineResourceReader,
        IResourceWriter,
        ISourceWatcher,
        ITryContextualResourceIdentity
{
    private const int RevisionMapLimit = 8;
    private const string MissingEntryRevision = "missing";
    private readonly ZipEntryResource _entry;
    private readonly object _revisionGate = new();
    private readonly Dictionary<
        (SubjectKey Key, RouteKey Route, string Revision),
        string?
    > _archiveRevisions = [];
    private readonly Queue<(SubjectKey Key, RouteKey Route, string Revision)> _revisionOrder =
        new();
    private readonly Dictionary<
        (SubjectKey Key, RouteKey Route, string Revision),
        string
    > _entryRevisionsByArchiveRevision = [];
    private readonly Queue<(
        SubjectKey Key,
        RouteKey Route,
        string Revision
    )> _archiveRevisionOrder = new();

    public SingleBinaryEntryResource(ZipEntryResource entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entry = entry;
    }

    public ResourceId ResourceId => _entry.ResourceId;

    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        _entry.GetResourceId(context);

    public bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId resourceId) =>
        _entry.TryGetResourceId(context, out resourceId);

    public bool IsPipelineReadPreferred => false;

    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var result = await _entry.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        if (result.Status == StateReadStatus.Unavailable)
        {
            return result;
        }

        var entryRevision =
            result.Status == StateReadStatus.NotFound
                ? MissingEntryRevision
                : GetEntryRevision(result.Content.Span);
        var exposedRevision = entryRevision;
        StoreArchiveRevision(context, entryRevision, result.Revision);
        StoreEntryRevision(context, result.Revision, entryRevision);
        return result with { Revision = exposedRevision };
    }

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

    public ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var archiveRevision = ResolveArchiveRevision(context, request.Condition.Revision);
        return WriteEntryAsync(
            new ResourceWriteRequest(
                request.Content,
                Condition: request.Condition.IsMatch
                    ? RevisionCondition.FromRevision(archiveRevision)
                    : request.Condition,
                Schema: request.Schema
            ),
            request.Content,
            context,
            cancellationToken
        );
    }

    public ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    ) =>
        _entry.WaitForChangeAsync(
            context,
            ResolveArchiveRevision(context, observedRevision),
            cancellationToken
        );

    private void StoreArchiveRevision(
        ConfiglueResourceContext context,
        string entryRevision,
        string? archiveRevision
    )
    {
        var key = (context.Key, context.Route, entryRevision);
        lock (_revisionGate)
        {
            if (_archiveRevisions.ContainsKey(key))
            {
                _archiveRevisions[key] = archiveRevision;
                return;
            }

            _archiveRevisions.Add(key, archiveRevision);
            _revisionOrder.Enqueue(key);
            while (_revisionOrder.Count > RevisionMapLimit)
            {
                _archiveRevisions.Remove(_revisionOrder.Dequeue());
            }
        }
    }

    private void StoreEntryRevision(
        ConfiglueResourceContext context,
        string? archiveRevision,
        string entryRevision
    )
    {
        var key = (context.Key, context.Route, archiveRevision ?? string.Empty);
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

    private bool HasUnchangedEntryAtRevision(
        ConfiglueResourceContext context,
        string? archiveRevision,
        string currentEntryRevision
    )
    {
        if (archiveRevision is null)
        {
            return false;
        }

        lock (_revisionGate)
        {
            return _entryRevisionsByArchiveRevision.TryGetValue(
                    (context.Key, context.Route, archiveRevision),
                    out var baseline
                ) && string.Equals(baseline, currentEntryRevision, StringComparison.Ordinal);
        }
    }

    private string? ResolveArchiveRevision(ConfiglueResourceContext context, string? entryRevision)
    {
        if (entryRevision is null)
        {
            return null;
        }

        lock (_revisionGate)
        {
            return _archiveRevisions.TryGetValue(
                (context.Key, context.Route, entryRevision),
                out var archiveRevision
            )
                ? archiveRevision
                : entryRevision;
        }
    }

    private async ValueTask<StateWriteResult> WriteEntryAsync(
        ResourceWriteRequest request,
        ReadOnlyMemory<byte> content,
        ConfiglueResourceContext context,
        CancellationToken cancellationToken
    )
    {
        var current = await _entry.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        if (current.Status == StateReadStatus.Unavailable)
        {
            throw new IOException("The ZIP entry is unavailable for writing.");
        }

        var currentRevision =
            current.Status == StateReadStatus.NotFound
                ? MissingEntryRevision
                : GetEntryRevision(current.Content.Span);
        var expectedRevision = request.Condition.Revision ?? MissingEntryRevision;
        var expectedRevisionMatches =
            string.Equals(expectedRevision, currentRevision, StringComparison.Ordinal)
            || string.Equals(expectedRevision, current.Revision, StringComparison.Ordinal)
            || HasUnchangedEntryAtRevision(context, request.Condition.Revision, currentRevision);
        if (
            request.Condition.IsMatch && !expectedRevisionMatches
            || request.Condition.IsMustNotExist && current.Status != StateReadStatus.NotFound
        )
        {
            throw new StateConflictException(
                $"The ZIP entry '{_entry.EntryName}' changed after the configuration state was read."
            );
        }

        await _entry
            .WriteAsync(
                context,
                new ResourceWriteRequest(
                    content,
                    Condition: RevisionCondition.FromRevision(current.Revision),
                    Schema: request.Schema
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        var written = await _entry.ReadAsync(context, cancellationToken).ConfigureAwait(false);
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

        StoreArchiveRevision(context, writtenRevision, written.Revision);
        StoreEntryRevision(context, written.Revision, writtenRevision);
        return new StateWriteResult(writtenRevision);
    }

    private static string GetEntryRevision(ReadOnlySpan<byte> content) =>
        "entry:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
}
