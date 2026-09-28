using System.Buffers;
using System.IO.Hashing;
using System.IO.Pipelines;
using System.Security.Cryptography;

namespace Configlue.Resources;

/// <summary>A resource result whose successful content is held in a disposable pipeline.</summary>
public sealed class PipelineResourceReadResult : IAsyncDisposable
{
    private readonly PipeReader? _content;
    private readonly IAsyncDisposable? _owner;
    private readonly Action<ReadOnlySequence<byte>>? _contentReadCompleted;
    private readonly ObservedReadStream? _observedStream;

    internal PipelineResourceReadResult(
        StateReadStatus status,
        PipeReader? content,
        string? revision,
        StateSchemaMetadata? schema,
        IAsyncDisposable? owner = null,
        Action<ReadOnlySequence<byte>>? contentReadCompleted = null,
        ObservedReadStream? observedStream = null
    )
    {
        if (status == StateReadStatus.Success && content is null)
        {
            throw new ArgumentNullException(nameof(content));
        }

        if (status != StateReadStatus.Success && content is not null)
        {
            throw new ArgumentException(
                "Only successful pipeline results can carry content.",
                nameof(content)
            );
        }

        Status = status;
        _content = content;
        _owner = owner;
        _contentReadCompleted = contentReadCompleted;
        _observedStream = observedStream;
        Revision = revision;
        Schema = schema;
    }

    /// <summary>The resource read status.</summary>
    public StateReadStatus Status { get; }

    /// <summary>
    /// The pipeline reader for successful content; valid until this result is disposed. Use
    /// <see cref="ReadAllAsync(CancellationToken)"/> or consume the reader to the end to finalize byte-derived
    /// revision data and provider completion callbacks.
    /// </summary>
    public PipeReader? Content => _content;

    /// <summary>The physical resource revision, finalized after reading content when computed from bytes.</summary>
    public string? Revision
    {
        get => _observedStream?.Revision ?? _revision;
        private set => _revision = value;
    }

    private string? _revision;

    /// <summary>Optional schema metadata supplied by the physical resource.</summary>
    public StateSchemaMetadata? Schema { get; }

    /// <summary>Creates a successful result and transfers ownership of the reader.</summary>
    public static PipelineResourceReadResult Success(
        PipeReader content,
        string? revision = null,
        StateSchemaMetadata? schema = null
    )
    {
        ArgumentNullException.ThrowIfNull(content);
        return new PipelineResourceReadResult(StateReadStatus.Success, content, revision, schema);
    }

    internal static PipelineResourceReadResult SuccessFromStream(
        Stream content,
        string? revision = null,
        StateSchemaMetadata? schema = null,
        IAsyncDisposable? owner = null,
        bool computeXxHash3Revision = false,
        Action<ReadOnlySequence<byte>>? contentReadCompleted = null,
        Action<string>? contentFingerprintCompleted = null
    )
    {
        ArgumentNullException.ThrowIfNull(content);
        ObservedReadStream? observedStream = null;
        if (computeXxHash3Revision || contentFingerprintCompleted is not null)
        {
            observedStream = new ObservedReadStream(
                content,
                computeXxHash3Revision,
                contentFingerprintCompleted
            );
            content = observedStream;
        }

        var reader = PipeReader.Create(
            content,
            new StreamPipeReaderOptions(bufferSize: 81920, minimumReadSize: 4096, leaveOpen: true)
        );
        return new PipelineResourceReadResult(
            StateReadStatus.Success,
            reader,
            revision,
            schema,
            owner ?? (observedStream is null ? content as IAsyncDisposable : null),
            contentReadCompleted,
            observedStream
        );
    }

    /// <summary>Creates a missing-resource result.</summary>
    public static PipelineResourceReadResult NotFound(string? revision = null) =>
        new(StateReadStatus.NotFound, null, revision, null);

    /// <summary>Creates a temporarily unavailable result.</summary>
    public static PipelineResourceReadResult Unavailable(string? revision = null) =>
        new(StateReadStatus.Unavailable, null, revision, null);

    /// <summary>
    /// Reads all content without consuming the final buffer, which remains valid until disposal. The caller
    /// must advance the reader to the returned sequence's end before disposing the result. This method also
    /// finalizes byte-derived revision data and provider completion callbacks.
    /// </summary>
    public async ValueTask<ReadOnlySequence<byte>> ReadAllAsync(
        CancellationToken cancellationToken = default
    )
    {
        var reader = _content ?? throw new InvalidOperationException("The result has no content.");
        while (true)
        {
            var read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = read.Buffer;
            if (read.IsCanceled)
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException("The pipeline read was canceled.");
            }

            if (read.IsCompleted)
            {
                _contentReadCompleted?.Invoke(buffer);
                return buffer;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>Consumes any remaining pipeline data and finalizes byte-derived observers.</summary>
    internal async ValueTask DrainAsync(CancellationToken cancellationToken = default)
    {
        var reader = _content ?? throw new InvalidOperationException("The result has no content.");
        while (true)
        {
            var read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = read.Buffer;
            reader.AdvanceTo(buffer.End);
            if (read.IsCanceled)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException("The pipeline read was canceled.");
            }

            if (read.IsCompleted)
            {
                return;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_content is not null)
            {
                await _content.CompleteAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                if (_observedStream is not null)
                {
                    await _observedStream.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                if (_owner is not null)
                {
                    await _owner.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    internal sealed class ObservedReadStream : Stream
    {
        private readonly Stream _source;
        private readonly XxHash3? _revisionHasher;
        private readonly IncrementalHash? _fingerprintHasher;
        private readonly Action<string>? _fingerprintCompleted;
        private bool _completed;
        private string? _revision;

        public ObservedReadStream(
            Stream source,
            bool computeXxHash3Revision,
            Action<string>? fingerprintCompleted
        )
        {
            _source = source;
            _revisionHasher = computeXxHash3Revision ? new XxHash3() : null;
            _fingerprintHasher = fingerprintCompleted is null
                ? null
                : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            _fingerprintCompleted = fingerprintCompleted;
        }

        public string? Revision => _revision;

        public override bool CanRead => _source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _source.Read(buffer, offset, count);
            Observe(buffer.AsSpan(offset, read), read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = _source.Read(buffer);
            Observe(buffer[..read], read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            var read = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Observe(buffer.Span[..read], read);
            return read;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        )
        {
            var read = await _source
                .ReadAsync(buffer, offset, count, cancellationToken)
                .ConfigureAwait(false);
            Observe(buffer.AsSpan(offset, read), read);
            return read;
        }

        private void Observe(ReadOnlySpan<byte> content, int read)
        {
            if (read > 0)
            {
                _revisionHasher?.Append(content);
                _fingerprintHasher?.AppendData(content);
                return;
            }

            if (_completed)
            {
                return;
            }

            _completed = true;
            if (_revisionHasher is not null)
            {
                Span<byte> hash = stackalloc byte[sizeof(ulong)];
                _revisionHasher.GetCurrentHash(hash);
                _revision = Convert.ToHexString(hash);
            }

            if (_fingerprintHasher is not null)
            {
                _fingerprintCompleted!(Convert.ToHexString(_fingerprintHasher.GetHashAndReset()));
                _fingerprintHasher.Dispose();
            }
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fingerprintHasher?.Dispose();
                _source.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            _fingerprintHasher?.Dispose();
            await _source.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}

/// <summary>Helpers for producing pipeline-backed resource results.</summary>
public static class PipelineResourceReader
{
    private static readonly PipeOptions PipeOptions = new(
        pauseWriterThreshold: long.MaxValue,
        resumeWriterThreshold: long.MaxValue / 2,
        minimumSegmentSize: 4096,
        useSynchronizationContext: false
    );

    /// <summary>Creates a pipeline configured to retain the complete resource until its reader consumes it.</summary>
    public static Pipe CreatePipe() => new(PipeOptions);

    /// <summary>Copies an existing resource result into a segmented pipeline.</summary>
    public static async ValueTask<PipelineResourceReadResult> FromMemoryAsync(
        ResourceReadResult result,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Status != StateReadStatus.Success)
        {
            return new PipelineResourceReadResult(
                result.Status,
                null,
                result.Revision,
                result.Schema
            );
        }

        var pipe = CreatePipe();
        var writerCompleted = false;
        try
        {
            var remaining = result.Content;
            while (!remaining.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(remaining.Length, 16 * 1024);
                remaining.Span[..length].CopyTo(pipe.Writer.GetSpan(length));
                pipe.Writer.Advance(length);
                remaining = remaining[length..];
            }

            await pipe.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            await pipe.Writer.CompleteAsync().ConfigureAwait(false);
            writerCompleted = true;
            return PipelineResourceReadResult.Success(pipe.Reader, result.Revision, result.Schema);
        }
        finally
        {
            if (!writerCompleted)
            {
                try
                {
                    await pipe.Writer.CompleteAsync().ConfigureAwait(false);
                }
                finally
                {
                    await pipe.Reader.CompleteAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>Creates a pipeline-backed result over a stream.</summary>
    public static PipelineResourceReadResult FromStream(
        Stream source,
        string? revision = null,
        StateSchemaMetadata? schema = null,
        bool computeXxHash3Revision = false,
        IDisposable? owner = null,
        Action<ReadOnlySequence<byte>>? contentReadCompleted = null,
        Action<string>? contentFingerprintCompleted = null
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        return PipelineResourceReadResult.SuccessFromStream(
            source,
            revision,
            schema,
            owner: owner is null ? null : new AsyncDisposableAdapter(owner),
            computeXxHash3Revision: computeXxHash3Revision,
            contentReadCompleted: contentReadCompleted,
            contentFingerprintCompleted: contentFingerprintCompleted
        );
    }

    private sealed class AsyncDisposableAdapter(IDisposable disposable) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            disposable.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
