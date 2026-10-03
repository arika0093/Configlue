using System.Text;
using Configlue.Provider.Json;
using Configlue.Sources;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class SerializedStateSourceCompositionTests
{
    [Test]
    public async Task ReaderOnlyAndWriterOnlyMiddlewarePreserveIndependentCapabilities()
    {
        var readOnlyResource = new InMemoryResource();
        await readOnlyResource.WriteAsync(
            new ResourceWriteRequest(System.Text.Encoding.UTF8.GetBytes("\"value\""))
        );
        var readOnly = SerializedStateSource.FromResource<string>(
            "reader-only",
            new ReaderOnlyResource(readOnlyResource),
            new JsonStateCodec<string>(),
            middlewares: [new ReaderSuffixMiddleware("-read")]
        );

        readOnly.Writer.ShouldBeNull();
        (await readOnly.ReadAsync()).Value.ShouldBe("value-read");

        var writableResource = new InMemoryResource();
        var writeOnly = SerializedStateSource.FromResource<string>(
            "writer-only",
            writableResource,
            new JsonStateCodec<string>(),
            middlewares: [new WriterSuffixMiddleware("-write")]
        );

        writeOnly.Writer.ShouldNotBeNull();
        await writeOnly.WriteAsync(new StateWriteRequest<string>("value"));
        (await writeOnly.ReadAsync()).Value.ShouldBe("value-write");
    }

    [Test]
    public async Task ReaderMiddlewarePreservesCancellationAndExceptions()
    {
        var source = SerializedStateSource.FromResource<string>(
            "reader-errors",
            new InMemoryResource(),
            new JsonStateCodec<string>(),
            middlewares: [new FailingReaderMiddleware()]
        );

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await source.ReadAsync(cancellationToken: new CancellationToken(canceled: true))
        );
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await source.ReadAsync()
        );
    }

    [Test]
    public async Task FromResource_ComposesOrderedTransformsAndStateMiddleware()
    {
        var resource = new InMemoryResource();
        var source = SerializedStateSource.FromResource<string>(
            "composed",
            resource,
            new JsonStateCodec<string>(),
            transformers: [new PrefixTransformer("outer:"), new PrefixTransformer("inner:")],
            middlewares: [new SuffixMiddleware("-first"), new SuffixMiddleware("-second")]
        );

        var write = await source.Writer!.WriteAsync(new StateWriteRequest<string>("value"));
        var rawContent = await resource.ReadAsync();
        var read = await source.Reader.ReadAsync();

        write.Revision.ShouldBe(rawContent.Revision);
        Encoding
            .UTF8.GetString(rawContent.Content.Span)
            .ShouldBe("outer:inner:\"value-first-second\"");
        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value.ShouldBe("value-first-second-second-first");
    }

    [Test]
    public async Task FromResource_ComposesAsyncOnlyTransformsInDefinedOrder()
    {
        var resource = new InMemoryResource();
        var order = new List<string>();
        var source = SerializedStateSource.FromResource<string>(
            "async-composed",
            resource,
            new JsonStateCodec<string>(),
            transformers: [
                new AsyncPrefixTransformer("outer:", "outer", order),
                new AsyncPrefixTransformer("inner:", "inner", order),
            ]
        );

        await source.Writer!.WriteAsync(new StateWriteRequest<string>("async"));
        var raw = await resource.ReadAsync();
        raw.Content.ToArray().ShouldBe(Encoding.UTF8.GetBytes("outer:inner:\"async\""));
        var result = await source.Reader.ReadAsync();
        result.Status.ShouldBe(StateReadStatus.Success);
        result.Value.ShouldBe("async");
        order.ShouldBe(new[] { "write:inner", "write:outer", "read:outer", "read:inner" });
    }

    [Test]
    public async Task TransformingResourceRunsAsyncOnlyTransformsWithoutBlocking()
    {
        var resource = new InMemoryResource();
        var order = new List<string>();
        var transforming = new TransformingResource(
            resource,
            [new AsyncPrefixTransformer("wrapped:", "wrapped", order)]
        );
        await transforming.Writer!.WriteAsync(
            ConfiglueResourceContext.Default,
            new ResourceWriteRequest(Encoding.UTF8.GetBytes("payload"))
        );
        Encoding.UTF8.GetString((await resource.ReadAsync()).Content.Span).ShouldBe("wrapped:payload");
        Encoding.UTF8.GetString((await transforming.ReadAsync(ConfiglueResourceContext.Default)).Content.Span)
            .ShouldBe("payload");
        order.ShouldBe(new[] { "write:wrapped", "read:wrapped" });
    }

    [Test]
    public async Task AsyncOnlyTransformReceivesCancellation()
    {
        var resource = new InMemoryResource();
        using var cancellation = new CancellationTokenSource();
        var transformer = new BlockingAsyncTransformer();
        var source = SerializedStateSource.FromResource<string>(
            "async-cancel",
            resource,
            new JsonStateCodec<string>(),
            transformers: [transformer]
        );

        var write = source.Writer!.WriteAsync(
            ConfiglueResourceContext.Default,
            new StateWriteRequest<string>("value"),
            cancellation.Token
        );
        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await write);
    }

    [Test]
    public async Task AsyncTransformParticipatesInValidatedBackupRecovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Configlue.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "state.json");
            using var resource = new FileResource(
                path,
                new FileResourceOptions { AutomaticBackupRecovery = true }
            );
            var source = SerializedStateSource.FromResource<string>(
                "async-recovery",
                resource,
                new JsonStateCodec<string>(),
                transformers: [new RecoverableAsyncPrefixTransformer("async:")]
            );
            await source.Writer!.WriteAsync(new StateWriteRequest<string>("backup"));
            await source.Writer!.WriteAsync(new StateWriteRequest<string>("current"));
            await File.WriteAllTextAsync(path, "bad-transform-payload");

            var recovered = await source.Reader.ReadAsync();

            recovered.Status.ShouldBe(StateReadStatus.Success);
            recovered.Value.ShouldBe("backup");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class AsyncPrefixTransformer(string prefix, string name, List<string> order)
        : IAsyncStateByteTransformer
    {
        public async ValueTask<ReadOnlyMemory<byte>> TransformReadAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default
        )
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            order.Add($"read:{name}");
            var text = Encoding.UTF8.GetString(source.Span);
            text.StartsWith(prefix, StringComparison.Ordinal).ShouldBeTrue();
            return Encoding.UTF8.GetBytes(text[prefix.Length..]);
        }

        public async ValueTask<ReadOnlyMemory<byte>> TransformWriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default
        )
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            order.Add($"write:{name}");
            return Encoding.UTF8.GetBytes(prefix + Encoding.UTF8.GetString(source.Span));
        }
    }

    private sealed class RecoverableAsyncPrefixTransformer(string prefix)
        : IAsyncStateByteTransformer,
            IStateByteTransformerRecoveryPolicy
    {
        public async ValueTask<ReadOnlyMemory<byte>> TransformReadAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default
        )
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            var text = Encoding.UTF8.GetString(source.Span);
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The async transform prefix is missing.");
            }
            return Encoding.UTF8.GetBytes(text[prefix.Length..]);
        }

        public async ValueTask<ReadOnlyMemory<byte>> TransformWriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default
        )
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return Encoding.UTF8.GetBytes(prefix + Encoding.UTF8.GetString(source.Span));
        }

        public bool IsRecoverableReadException(Exception exception) =>
            exception is InvalidDataException;
    }

    private sealed class BlockingAsyncTransformer : IAsyncStateByteTransformer
    {
        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public ValueTask<ReadOnlyMemory<byte>> TransformReadAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default
        ) => new(source);

        public async ValueTask<ReadOnlyMemory<byte>> TransformWriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default
        )
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return source;
        }
    }

    private sealed class PrefixTransformer(string prefix) : ISynchronousStateByteTransformer
    {
        public ReadOnlyMemory<byte> TransformRead(ReadOnlyMemory<byte> source)
        {
            var text = Encoding.UTF8.GetString(source.Span);
            text.StartsWith(prefix, StringComparison.Ordinal).ShouldBeTrue();
            return Encoding.UTF8.GetBytes(text[prefix.Length..]);
        }

        public ReadOnlyMemory<byte> TransformWrite(ReadOnlyMemory<byte> source) =>
            Encoding.UTF8.GetBytes(prefix + Encoding.UTF8.GetString(source.Span));
    }

    private sealed class SuffixMiddleware(string suffix)
        : IStateReaderMiddleware<string>, IStateWriterMiddleware<string>
    {
        public ISourceReader<string> WrapReader(ISourceReader<string> next) =>
            new SuffixReader(next, suffix);

        public ISourceWriter<string> WrapWriter(ISourceWriter<string> next) =>
            new SuffixWriter(next, suffix);
    }

    private sealed class ReaderSuffixMiddleware(string suffix) : IStateReaderMiddleware<string>
    {
        public ISourceReader<string> WrapReader(ISourceReader<string> next) =>
            new SuffixReader(next, suffix);
    }

    private sealed class WriterSuffixMiddleware(string suffix) : IStateWriterMiddleware<string>
    {
        public ISourceWriter<string> WrapWriter(ISourceWriter<string> next) =>
            new SuffixWriter(next, suffix);
    }

    private sealed class FailingReaderMiddleware : IStateReaderMiddleware<string>
    {
        public ISourceReader<string> WrapReader(ISourceReader<string> next) => new FailingReader();
    }

    private sealed class FailingReader : ISourceReader<string>
    {
        public ValueTask<StateReadResult<string>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("middleware failure");
        }
    }

    private sealed class ReaderOnlyResource(IResourceReader inner) : IResourceReader
    {
        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => inner.ReadAsync(context, cancellationToken);
    }

    private sealed class SuffixReader(ISourceReader<string> next, string suffix)
        : ISourceReader<string>
    {
        public async ValueTask<StateReadResult<string>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            var result = await next.ReadAsync(context, cancellationToken);
            return result.Status == StateReadStatus.Success
                ? StateReadResult<string>.Success(
                    result.Value + suffix,
                    result.Revision,
                    result.Schema
                ) with
                {
                    SourceId = result.SourceId,
                    PhysicalOrigin = result.PhysicalOrigin,
                    Revisions = result.Revisions,
                }
                : result;
        }
    }

    private sealed class SuffixWriter(ISourceWriter<string> next, string suffix)
        : ISourceWriter<string>
    {
        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<string> request,
            CancellationToken cancellationToken = default
        ) =>
            next.WriteAsync(
                context,
                new StateWriteRequest<string>(request.Value + suffix, request.Condition),
                cancellationToken
            );
    }
}
