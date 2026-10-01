using System.Buffers;
using Configlue.Provider.MessagePack;
using Configlue.State;

namespace Configlue.Tests;

public sealed class MessagePackMigrationTests
{
    [Test]
    public void SchemaDispatcher_MigratesHistoricalMessagePackPayload()
    {
        var previousCodec = new MessagePackStateCodec<MessagePackPreviousSampleSettings.Fragment>();
        var previousFragment = MessagePackPreviousSampleSettings.Fragment.From(
            new MessagePackPreviousSampleSettings { Name = "legacy", Count = 7 }
        );
        var buffer = new ArrayBufferWriter<byte>();
        previousCodec.Serialize(previousFragment, buffer, default);
        var source = new ReadOnlySequence<byte>(buffer.WrittenMemory);

        var schema = previousCodec.ReadSchemaMetadata(source);
        schema.ShouldBe(new StateSchemaMetadata("messagepack.sample", 1));

        var dispatcher = MessagePackSampleSettings.CreateSchemaDispatcher(
            new MessagePackStateCodec<MessagePackPreviousSampleSettings.Fragment>()
        );
        var handled = dispatcher.TryDeserialize(schema!.Value, source, null, out var migrated);

        handled.ShouldBeTrue();
        migrated!.Name.Value.ShouldBe("legacy");
        migrated.Count.Value.ShouldBe(7);
    }
}
