using System.Text;
using Configlue.Provider.Json;
using Configlue.Sources;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class SerializedStateSourceCompositionTests
{
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

    private sealed class PrefixTransformer(string prefix) : IStateByteTransformer
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

    private sealed class SuffixMiddleware(string suffix) : IStateMiddleware<string>
    {
        public ISourceReader<string> WrapReader(ISourceReader<string> next) =>
            new SuffixReader(next, suffix);

        public ISourceWriter<string> WrapWriter(ISourceWriter<string> next) =>
            new SuffixWriter(next, suffix);
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
                request with { Value = request.Value + suffix },
                cancellationToken
            );
    }
}
