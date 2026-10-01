using System.Buffers;
using Configlue;
using Configlue.Provider.MessagePack;
using MessagePack;

namespace Configlue.Tests;

[ConfiglueModel("messagepack.wire")]
public partial class MessagePackWireSettings
{
    public string? Name { get; set; }
    public int Count { get; set; }
    public MessagePackNestedPoco? Child { get; set; }
}

[ConfiglueModel("messagepack.wire")]
public partial class MessagePackReorderedWireSettings
{
    public MessagePackNestedPoco? Child { get; set; }
    public int Count { get; set; }
    public string? Name { get; set; }
}

public sealed class MessagePackWireContractTests
{
    [Test]
    public void FragmentWireKeys_DoNotDependOnDeclarationOrder()
    {
        var serializer = new MessagePackStateValueSerializer<MessagePackWireSettings.Fragment>();
        var buffer = new ArrayBufferWriter<byte>();
        serializer.Serialize(
            new MessagePackWireSettings.Fragment { Name = "wire", Count = 0 },
            buffer
        );

        var reader = new MessagePackReader(buffer.WrittenMemory);
        reader.ReadMapHeader().ShouldBe(2);
        var keys = new HashSet<string>();
        for (var index = 0; index < 2; index++)
        {
            keys.Add(reader.ReadString()!);
            reader.Skip();
        }
        keys.SetEquals(["Name", "Count"]).ShouldBeTrue();
        var reordered =
            new MessagePackStateValueSerializer<MessagePackReorderedWireSettings.Fragment>().Deserialize(
                new ReadOnlySequence<byte>(buffer.WrittenMemory)
            )!;
        reordered.Name.Value.ShouldBe("wire");
        reordered.Count.IsPresent.ShouldBeTrue();
        reordered.Count.Value.ShouldBe(0);
        reordered.Child.IsPresent.ShouldBeFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void EmptyFragments_AndPresentNull_RoundTrip(bool presentNull)
    {
        var codec = new MessagePackStateCodec<MessagePackWireSettings.Fragment>();
        var value = presentNull
            ? new MessagePackWireSettings.Fragment
            {
                Name = Optional<string?>.Present(null),
                Child = null,
            }
            : new MessagePackWireSettings.Fragment();
        var buffer = new ArrayBufferWriter<byte>();
        codec.Serialize(value, buffer, default);
        var read = codec.Deserialize(new ReadOnlySequence<byte>(buffer.WrittenMemory), default)!;
        read.Name.IsPresent.ShouldBe(presentNull);
        read.Child.IsPresent.ShouldBe(presentNull);
        if (presentNull)
        {
            read.Name.Value.ShouldBeNull();
            read.Child.Value.ShouldBeNull();
        }
        read.Count.IsPresent.ShouldBeFalse();

        var bare = new ArrayBufferWriter<byte>();
        new MessagePackStateValueSerializer<MessagePackWireSettings.Fragment>().Serialize(
            value,
            bare
        );
        codec.ReadSchemaMetadata(new ReadOnlySequence<byte>(bare.WrittenMemory)).ShouldBeNull();
        codec
            .Deserialize(new ReadOnlySequence<byte>(bare.WrittenMemory), default)!
            .Name.IsPresent.ShouldBe(presentNull);
    }

    [Test]
    public void Envelope_IgnoresUnknownEntriesBeforeMetadataAndPayload()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteMapHeader(3);
        writer.Write("future");
        writer.WriteArrayHeader(2);
        writer.Write(123);
        writer.WriteNil();
        writer.Write("$value");
        writer.WriteMapHeader(2);
        writer.Write("Unknown");
        writer.WriteMapHeader(1);
        writer.Write("nested");
        writer.Write(true);
        writer.Write("Name");
        writer.Write("forward compatible");
        writer.Write("$configlue");
        writer.WriteMapHeader(2);
        writer.Write("id");
        writer.Write("messagepack.wire");
        writer.Write("version");
        writer.Write(1);
        writer.Flush();

        var codec = new MessagePackStateCodec<MessagePackWireSettings.Fragment>();
        var sequence = new ReadOnlySequence<byte>(buffer.WrittenMemory);
        codec
            .ReadSchemaMetadata(sequence)
            .ShouldBe(new State.StateSchemaMetadata("messagepack.wire", 1));
        codec.Deserialize(sequence, default)!.Name.Value.ShouldBe("forward compatible");
    }

    [Test]
    public void Codec_DoesNotClassifyWrappedMissingFormattersAsCorruptContent()
    {
        var codec = new MessagePackStateCodec<MessagePackWireSettings.Fragment>();
        var exception = new MessagePackSerializationException(
            "outer",
            new MessagePackSerializationException(
                "inner",
                new FormatterNotRegisteredException("missing")
            )
        );
        codec.IsRecoverableReadException(exception).ShouldBeFalse();
        new MessagePackStateCodec().IsRecoverableReadException(exception).ShouldBeFalse();
    }

    [Test]
    public void NestedFragments_RespectDepthLimit_AndRestoreReaderDepthOnFailure()
    {
        var serializer = new MessagePackStateValueSerializer<MessagePackWireSettings.Fragment>();
        var buffer = new ArrayBufferWriter<byte>();
        serializer.Serialize(
            MessagePackWireSettings.Fragment.From(
                new MessagePackWireSettings { Child = new MessagePackNestedPoco { Enabled = true } }
            ),
            buffer
        );
        var reader = new MessagePackReader(buffer.WrittenMemory);
        var options = MessagePackSerializerOptions.Standard.WithSecurity(
            MessagePackSecurity.UntrustedData.WithMaximumObjectGraphDepth(1)
        );
        var rejected = false;
        try
        {
            MessagePackWireSettings.Fragment.MessagePackFormatter.Deserialize(ref reader, options);
        }
        catch (InsufficientExecutionStackException)
        {
            rejected = true;
        }
        rejected.ShouldBeTrue();
        reader.Depth.ShouldBe(0);

        var codec = new MessagePackStateCodec<MessagePackWireSettings.Fragment>(options);
        var exception = Should.Throw<MessagePackSerializationException>(() =>
            codec.Deserialize(new ReadOnlySequence<byte>(buffer.WrittenMemory), default)
        );
        codec.IsRecoverableReadException(exception).ShouldBeTrue();
    }
}
