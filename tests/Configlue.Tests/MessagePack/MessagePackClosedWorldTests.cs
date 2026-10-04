using System.Buffers;
using Configlue;
using Configlue.Provider.MessagePack;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace Configlue.Tests;

[ConfiglueModel("messagepack.closed-world")]
public partial class ClosedWorldSettings
{
    public int Count { get; set; }

    public string? Label { get; set; }

    public int? RetryLimit { get; set; }

    public ClosedWorldStatus Status { get; set; }

    public ClosedWorldChild? Child { get; set; }

    public List<int> Numbers { get; set; } = [];

    public List<ClosedWorldPoco> Items { get; set; } = [];
}

[ConfiglueModel("messagepack.closed-world.child")]
public partial class ClosedWorldChild
{
    public string Name { get; set; } = string.Empty;

    public int Value { get; set; }
}

public enum ClosedWorldStatus
{
    Unknown,
    Active,
    Retired,
}

[MessagePackObject]
public partial class ClosedWorldPoco
{
    [Key(0)]
    public string Name { get; set; } = string.Empty;

    [Key(1)]
    public int Value { get; set; }
}

[GeneratedMessagePackResolver]
internal partial class ClosedWorldMessagePackResolver;

public sealed class MessagePackClosedWorldTests
{
    [Test]
    public void ClosedWorldOptions_RoundTripNestedNullableEnumAndCollections()
    {
        var options = ConfiglueMessagePackResolver.CreateClosedWorldOptions(
            ClosedWorldMessagePackResolver.Instance,
            new ClosedWorldCollectionResolver()
        );
        var codec = new MessagePackStateCodec<ClosedWorldSettings.Fragment>(options);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Serialize(
            ClosedWorldSettings.Fragment.From(
                new ClosedWorldSettings
                {
                    Count = 7,
                    Label = null,
                    RetryLimit = 3,
                    Status = ClosedWorldStatus.Active,
                    Child = new ClosedWorldChild { Name = "nested", Value = 23 },
                    Numbers = [1, 3, 5],
                    Items =
                    [
                        new ClosedWorldPoco { Name = "first", Value = 47 },
                        new ClosedWorldPoco { Name = "second", Value = 59 },
                    ],
                }
            ),
            buffer,
            default
        );

        var read = codec.Deserialize(new ReadOnlySequence<byte>(buffer.WrittenMemory), default)!;
        read.Count.Value.ShouldBe(7);
        read.Label.IsPresent.ShouldBeTrue();
        read.Label.Value.ShouldBeNull();
        read.RetryLimit.Value.ShouldBe(3);
        read.Status.Value.ShouldBe(ClosedWorldStatus.Active);
        read.Child.Value!.Name.Value.ShouldBe("nested");
        read.Child.Value!.Value.Value.ShouldBe(23);
        read.Numbers.Value!.SequenceEqual([1, 3, 5]).ShouldBeTrue();
        read.Items.Value!.Count.ShouldBe(2);
        read.Items.Value![0].Name.ShouldBe("first");
        read.Items.Value![1].Value.ShouldBe(59);
    }

    private sealed class ClosedWorldCollectionResolver : IFormatterResolver
    {
        private static readonly NullableStringFormatter LabelFormatter =
            NullableStringFormatter.Instance;
        private static readonly Int32Formatter CountFormatter = Int32Formatter.Instance;
        private static readonly NullableInt32Formatter RetryFormatter =
            NullableInt32Formatter.Instance;
        private static readonly ListFormatter<int> Numbers = new();
        private static readonly ListFormatter<ClosedWorldPoco> Items = new();

        public IMessagePackFormatter<T>? GetFormatter<T>()
        {
            if (typeof(T) == typeof(string))
            {
                return (IMessagePackFormatter<T>)(object)LabelFormatter;
            }

            if (typeof(T) == typeof(int))
            {
                return (IMessagePackFormatter<T>)(object)CountFormatter;
            }

            if (typeof(T) == typeof(int?))
            {
                return (IMessagePackFormatter<T>)(object)RetryFormatter;
            }

            if (typeof(T) == typeof(List<int>))
            {
                return (IMessagePackFormatter<T>)(object)Numbers;
            }

            if (typeof(T) == typeof(List<ClosedWorldPoco>))
            {
                return (IMessagePackFormatter<T>)(object)Items;
            }

            if (typeof(T) == typeof(ClosedWorldStatus))
            {
                return (IMessagePackFormatter<T>)
                    (object)MessagePackEnumFormatter<ClosedWorldStatus>.Instance;
            }

            return null;
        }
    }
}
