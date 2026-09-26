using System.Security.Cryptography;

namespace Configlue.Testing;

/// <summary>An in-memory resource with conditional writes and change notifications.</summary>
public sealed class InMemoryResource : IResourceReader, IResourceWriter, IStateWatcher
{
    private readonly object _gate = new();
    private byte[]? _content;
    private string? _revision;
    private StateSchemaMetadata? _schema;
    private TaskCompletionSource _changed = NewSignal();

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
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource changed;
        string revision;
        lock (_gate)
        {
            if (request.ExpectedRevision is not null && !string.Equals(request.ExpectedRevision, _revision, StringComparison.Ordinal))
            {
                throw new StateConflictException("The in-memory resource changed after it was read.");
            }

            _content = request.Content.ToArray();
            _revision = revision = GetRevision(_content);
            _schema = request.Schema;
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
