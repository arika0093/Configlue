using System.Text;
using Configlue.Provider.Json;
using Configlue.Sources;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class SerializedSourceCompositionTests
{
    [Test]
    public async Task ReaderOnlyAndWriterOnlyMiddlewarePreserveIndependentCapabilities()
    {
        var readOnlyResource = new InMemoryResource();
        await readOnlyResource.WriteAsync(
            new ResourceWriteRequest(System.Text.Encoding.UTF8.GetBytes("\"value\""))
        );
        IResourceReader readerOnlySection = new ReaderOnlyResource(readOnlyResource);
        var readOnly = new StateSource<string>(
            "reader-only",
            new SerializedSource<string>(
                readerOnlySection,
                new JsonStateCodec<string>(),
                writer: readerOnlySection as IResourceWriter,
                watcher: readerOnlySection as ISourceWatcher,
                middlewares: [new ReaderSuffixMiddleware("-read")]
            ),
            new StateSourceOptions<string>()
        );

        readOnly.Writer.ShouldBeNull();
        (await readOnly.ReadAsync()).Value.ShouldBe("value-read");

        var writableResource = new InMemoryResource();
        var writeOnly = new StateSource<string>("writer-only", new SerializedSource<string>(writableResource, new JsonStateCodec<string>(), writer: (IResourceReader)writableResource as IResourceWriter, watcher: (IResourceReader)writableResource as ISourceWatcher, middlewares: [new WriterSuffixMiddleware("-write")]), new StateSourceOptions<string>());

        writeOnly.Writer.ShouldNotBeNull();
        await writeOnly.WriteAsync(new StateWriteRequest<string>("value"));
        (await writeOnly.ReadAsync()).Value.ShouldBe("value-write");
    }

    [Test]
    public async Task ReaderMiddlewarePreservesCancellationAndExceptions()
    {
        IResourceReader errorResource = new InMemoryResource();
        var source = new StateSource<string>(
            "reader-errors",
            new SerializedSource<string>(
                errorResource,
                new JsonStateCodec<string>(),
                writer: errorResource as IResourceWriter,
                watcher: errorResource as ISourceWatcher,
                middlewares: [new FailingReaderMiddleware()]
            ),
            new StateSourceOptions<string>()
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
        var source = new StateSource<string>("composed", new SerializedSource<string>(resource, new JsonStateCodec<string>(), transformers: [new PrefixTransformer("outer:"), new PrefixTransformer("inner:")], writer: (IResourceReader)resource as IResourceWriter, watcher: (IResourceReader)resource as ISourceWatcher, middlewares: [new SuffixMiddleware("-first"), new SuffixMiddleware("-second")]), new StateSourceOptions<string>());

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
        var source = new StateSource<string>("async-composed", new SerializedSource<string>(resource, new JsonStateCodec<string>(), transformers: [
                new AsyncPrefixTransformer("outer:", "outer", order),
                new AsyncPrefixTransformer("inner:", "inner", order),
            ], writer: (IResourceReader)resource as IResourceWriter, watcher: (IResourceReader)resource as ISourceWatcher), new StateSourceOptions<string>());

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
        var source = new StateSource<string>("async-cancel", new SerializedSource<string>(resource, new JsonStateCodec<string>(), transformers: [transformer], writer: (IResourceReader)resource as IResourceWriter, watcher: (IResourceReader)resource as ISourceWatcher), new StateSourceOptions<string>());

        var write = source.Writer!.WriteAsync(
            ConfiglueResourceContext.Default,
            new StateWriteRequest<string>("value"),
            cancellation.Token
        );
        await transformer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await write);
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
