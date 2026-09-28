using System.Text;
using Configlue.Provider.Json;
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
        public IStateReader<string> WrapReader(IStateReader<string> next) =>
            new SuffixReader(next, suffix);

        public IStateWriter<string> WrapWriter(IStateWriter<string> next) =>
            new SuffixWriter(next, suffix);
    }

    private sealed class SuffixReader(IStateReader<string> next, string suffix)
        : IStateReader<string>
    {
        public async ValueTask<StateReadResult<string>> ReadAsync(
            CancellationToken cancellationToken = default
        )
        {
            var result = await next.ReadAsync(cancellationToken);
            return result.Status == StateReadStatus.Success
                ? result with
                {
                    Value = result.Value + suffix,
                }
                : result;
        }
    }

    private sealed class SuffixWriter(IStateWriter<string> next, string suffix)
        : IStateWriter<string>
    {
        public ValueTask<StateWriteResult> WriteAsync(
            StateWriteRequest<string> request,
            CancellationToken cancellationToken = default
        ) => next.WriteAsync(request with { Value = request.Value + suffix }, cancellationToken);
    }
}
