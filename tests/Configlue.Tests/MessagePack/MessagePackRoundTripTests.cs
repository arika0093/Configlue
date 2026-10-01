using System.Buffers;
using Configlue.Provider.MessagePack;
using Configlue.State;
using MessagePack;

namespace Configlue.Tests;

public sealed class MessagePackRoundTripTests
{
    [Test]
    public void RoundTrip_PreservesSparseMembersSchemaAndNestedValues()
    {
        var fragment = MessagePackSampleSettings.Fragment.From(
            new MessagePackSampleSettings
            {
                Name = "alpha",
                Count = 3,
                Label = null,
                Items = ["x", "y"],
                Nested = new MessagePackNestedPoco { Enabled = true, Text = "text" },
                Accent = new MessagePackAccentColor(1, 2, 3),
            }
        );
        var codec = new MessagePackStateCodec<MessagePackSampleSettings.Fragment>(
            TestMessagePack.Options
        );
        var buffer = new ArrayBufferWriter<byte>();

        codec.Serialize(fragment, buffer, default);

        var schema = codec.ReadSchemaMetadata(Sequence(buffer));
        schema.ShouldBe(new StateSchemaMetadata("messagepack.sample", 2));

        var read = codec.Deserialize(Sequence(buffer), default)!;
        read.Name.Value.ShouldBe("alpha");
        read.Count.Value.ShouldBe(3);
        read.Label.IsPresent.ShouldBeTrue();
        read.Label.Value.ShouldBeNull();
        read.Items.Value!.SequenceEqual(["x", "y"]).ShouldBeTrue();
        read.Nested.Value!.Enabled.Value.ShouldBeTrue();
        read.Nested.Value!.Text.Value.ShouldBe("text");
        read.Accent.Value.ShouldBe(new MessagePackAccentColor(1, 2, 3));
        read.ToModel().Name.ShouldBe("alpha");
    }

    [Test]
    public void RoundTrip_LeavesAbsentMembersMissing()
    {
        var sparse = new MessagePackSampleSettings.Fragment { Name = "only" };
        var codec = new MessagePackStateCodec<MessagePackSampleSettings.Fragment>(
            TestMessagePack.Options
        );
        var buffer = new ArrayBufferWriter<byte>();

        codec.Serialize(sparse, buffer, default);
        var read = codec.Deserialize(Sequence(buffer), default)!;

        read.Name.IsPresent.ShouldBeTrue();
        read.Count.IsPresent.ShouldBeFalse();
        read.Items.IsPresent.ShouldBeFalse();
        read.Nested.IsPresent.ShouldBeFalse();
        read.Accent.IsPresent.ShouldBeFalse();
    }

    [Test]
    public void ValueSerializer_RoundTripsBarePayload()
    {
        var fragment = MessagePackSampleSettings.Fragment.From(
            new MessagePackSampleSettings { Name = "bare" }
        );
        var serializer = new MessagePackStateValueSerializer<MessagePackSampleSettings.Fragment>(
            TestMessagePack.Options
        );
        var buffer = new ArrayBufferWriter<byte>();

        serializer.Serialize(fragment, buffer);
        var read = serializer.Deserialize(Sequence(buffer))!;

        read.Name.Value.ShouldBe("bare");
    }

    [Test]
    public void Codec_ReportsMalformedInputAsRecoverableButNotMissingFormatters()
    {
        var codec = new MessagePackStateCodec<MessagePackSampleSettings.Fragment>();

        codec
            .IsRecoverableReadException(new MessagePackSerializationException("bad"))
            .ShouldBeTrue();
        codec
            .IsRecoverableReadException(new FormatterNotRegisteredException("missing"))
            .ShouldBeFalse();

        Should.Throw<MessagePackSerializationException>(() =>
            codec.Deserialize(new ReadOnlySequence<byte>(new byte[] { 0x92, 0x01 }), default)
        );
    }

    private static ReadOnlySequence<byte> Sequence(ArrayBufferWriter<byte> buffer) =>
        new(buffer.WrittenMemory);
}
