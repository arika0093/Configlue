using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Configlue.Provider.Json;

namespace Configlue.Tests;

public sealed class JsonSimplePropertyPlanTests
{
    [Test]
    public void FrozenPropertiesKeepStableOrderAndEvaluateEachCurrentValue()
    {
        var info = CreateInfo();
        info.Properties.Single(property => property.Name == nameof(Model.Counter)).ShouldSerialize =
            static (_, value) => (int)value! > 0;
        info.Options.MakeReadOnly();
        info.MakeReadOnly();
        var value = new Model { Counter = 42, Name = "first" };
        var first = Write(info, value);
        first.Fast.ShouldBeTrue();
        Names(first.Json).ShouldBe(["Name", "Counter", "Extra"]);

        value.Counter = 0;
        value.Name = "second";
        var second = Write(info, value);
        second.Fast.ShouldBeTrue();
        Names(second.Json).ShouldBe(["Name", "Extra"]);
        using var document = JsonDocument.Parse(second.Json);
        document.RootElement.GetProperty("Name").GetString().ShouldBe("second");
    }

    [Test]
    public void MutableMetadataRechecksOrderNamesAndEligibility()
    {
        var info = CreateInfo();
        var value = new Model { Counter = 42 };
        Names(Write(info, value).Json).ShouldBe(["Name", "Counter", "Extra"]);
        info.IsReadOnly.ShouldBeFalse();
        var counter = info.Properties.Single(property => property.Name == nameof(Model.Counter));
        counter.Order = -2;
        counter.Name = "renamed";
        Names(Write(info, value).Json).ShouldBe(["renamed", "Name", "Extra"]);
        counter.NumberHandling = JsonNumberHandling.WriteAsString;
        Write(info, value).Fast.ShouldBeFalse();
        counter.NumberHandling = null;
        Write(info, value).Fast.ShouldBeTrue();
    }

    [Test]
    public void FrozenIneligibleMetadataStillFallsBack()
    {
        var info = CreateInfo();
        info.Properties.Single(property => property.Name == nameof(Model.Counter)).NumberHandling =
            JsonNumberHandling.WriteAsString;
        info.Options.MakeReadOnly();
        info.MakeReadOnly();
        Write(info, new Model()).Fast.ShouldBeFalse();
        Write(info, new Model()).Fast.ShouldBeFalse();
    }

    private static JsonTypeInfo CreateInfo()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
        return resolver.GetTypeInfo(typeof(Model), options);
    }

    private static (bool Fast, string Json) Write(JsonTypeInfo info, Model value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        var schema = new StateSchemaMetadata("plain", 1);
        var context = new StateCodecContext(schema);
        var fast = JsonStateCodecOperations.TryWriteSimpleObjectPayload(
            writer,
            value,
            typeof(Model),
            info.Converter,
            info,
            info.Options,
            schema,
            in context,
            null
        );
        writer.Flush();
        return (fast, Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static string[] Names(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document
            .RootElement.EnumerateObject()
            .Skip(1)
            .Select(property => property.Name)
            .ToArray();
    }

    public sealed class Model
    {
        [JsonPropertyOrder(2)]
        public int Counter { get; set; }

        [JsonPropertyOrder(-1)]
        public string Name { get; set; } = "name";

        [JsonPropertyOrder(2)]
        public string Extra { get; set; } = "extra";
    }
}
