using System.Security.Cryptography;

namespace Configlue.Testing;

/// <summary>An in-memory resource with conditional writes and change notifications.</summary>
public sealed class InMemoryResource : IResourceReader, IResourceWriter, IStateWatcher, IResourceIdentity, IResourceBatchWriter
{
    private readonly object _gate = new();
    private byte[]? _content;
    private string? _revision;
    private StateSchemaMetadata? _schema;
    private TaskCompletionSource _changed = NewSignal();
    private long _writeCount;

    /// <summary>Creates a resource with a unique identity.</summary>
    public InMemoryResource() => ResourceId = new ResourceId($"memory:{Guid.NewGuid():N}");

    /// <inheritdoc />
    public ResourceId ResourceId { get; }

    /// <summary>The number of successful physical write operations.</summary>
    public long WriteCount => Interlocked.Read(ref _writeCount);

    /// <inheritdoc />
    public ValueTask<ResourceReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_content is null
                ? ResourceReadResult.NotFound()
                : ResourceReadResult.Success(_content.ToArray(), _revision, _schema));
        }
    }

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default) =>
        WriteBatchAsync([ResourceWriteMutation.Replace(request)], cancellationToken);

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteBatchAsync(
        IReadOnlyList<ResourceWriteMutation> mutations,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResourceWriteMutation.ValidateBatch(mutations);
        TaskCompletionSource changed;
        string revision;
        lock (_gate)
        {
            var expectedRevision = mutations[0].ExpectedRevision;
            var checkRevision = mutations.Any(static mutation => mutation.CheckRevision || mutation.ExpectedRevision is not null);
            if (checkRevision && !string.Equals(expectedRevision, _revision, StringComparison.Ordinal))
            {
                throw new StateConflictException("The in-memory resource changed after it was read.");
            }

            var current = _content is null
                ? ResourceReadResult.NotFound(_revision)
                : ResourceReadResult.Success(_content, _revision, _schema);
            foreach (var mutation in mutations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = mutation.Apply(current).ToArray();
                current = ResourceReadResult.Success(content, _revision);
            }

            _content = current.Content.ToArray();
            _revision = revision = GetRevision(_content);
            _schema = mutations.Select(static mutation => mutation.Schema).Distinct().Count() == 1
                ? mutations[0].Schema
                : null;
            Interlocked.Increment(ref _writeCount);
            changed = _changed;
            _changed = NewSignal();
        }

        changed.TrySetResult();
        return ValueTask.FromResult(new StateWriteResult(revision));
    }

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(
        string? observedRevision,
        CancellationToken cancellationToken = default)
    {
        Task waitTask;
        lock (_gate)
        {
            if (!string.Equals(_revision, observedRevision, StringComparison.Ordinal))
            {
                return;
            }

            waitTask = _changed.Task;
        }

        await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string GetRevision(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content));

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
