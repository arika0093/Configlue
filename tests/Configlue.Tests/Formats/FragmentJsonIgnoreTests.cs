using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Configlue.Provider.Json;

namespace Configlue.Tests;

[ConfiglueModel("jsonignore-200-child", Version = 1)]
public partial class JsonIgnore200Child
{
    public int Count { get; set; }

    [JsonIgnore]
    public string InnerSecret { get; set; } = string.Empty;
}

[ConfiglueModel("jsonignore-200-root", Version = 1)]
public partial class JsonIgnore200Root
{
    public int Value { get; set; }

    [JsonIgnore]
    public string Secret { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public string ExplicitSecret { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string NeverIgnored { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MaybeNull { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MaybeDefault { get; set; }

    public List<int> Tags { get; set; } = new();

    [JsonIgnore]
    public List<int> SecretTags { get; set; } = new();

    public JsonIgnore200Child? Child { get; set; }
}

public sealed class FragmentJsonIgnoreTests
{
    private static JsonIgnore200Root.Fragment ToFragment(JsonIgnore200Root model) =>
        JsonIgnore200Root.Fragment.From(model);

    private static string SerializeTyped(JsonIgnore200Root.Fragment fragment)
    {
        var codec = new JsonStateCodec<JsonIgnore200Root.Fragment>();
        var buffer = new ArrayBufferWriter<byte>();
        codec.Serialize(fragment, buffer, default);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string SerializeDynamic(JsonIgnore200Root.Fragment fragment)
    {
        var codec = new JsonStateCodec();
        var buffer = new ArrayBufferWriter<byte>();
        codec.Serialize(typeof(JsonIgnore200Root.Fragment), fragment, buffer, default);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string SerializePayloadWriter(JsonIgnore200Root.Fragment fragment)
    {
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        ((IJsonObjectPayloadWriter)new JsonIgnore200Root.Fragment.FragmentJsonConverter())
            .WriteObjectPayloadProperties(writer, fragment, options);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Test]
    public void Serialize_OmitsAlwaysIgnoredMembers()
    {
        var fragment = ToFragment(
            new JsonIgnore200Root
            {
                Value = 7,
                Secret = "private-value",
                ExplicitSecret = "explicit-private",
                NeverIgnored = "kept",
                Tags = new List<int> { 1, 2 },
                SecretTags = new List<int> { 9 },
                Child = new JsonIgnore200Child { Count = 1, InnerSecret = "inner-private" },
            }
        );

        foreach (var json in new[] { SerializeTyped(fragment), SerializeDynamic(fragment) })
        {
            json.ShouldContain("\"Value\":7");
            json.ShouldContain("\"NeverIgnored\":\"kept\"");
            json.ShouldContain("\"Tags\":[1,2]");
            json.ShouldContain("\"Child\"");
            json.ShouldContain("\"Count\":1");
            json.ShouldNotContain("private-value");
            json.ShouldNotContain("explicit-private");
            json.ShouldNotContain("Secret");
            json.ShouldNotContain("ExplicitSecret");
            json.ShouldNotContain("SecretTags");
            json.ShouldNotContain("inner-private");
            json.ShouldNotContain("InnerSecret");
        }

        var payload = SerializePayloadWriter(fragment);
        payload.ShouldNotContain("private-value");
        payload.ShouldNotContain("InnerSecret");
        payload.ShouldContain("\"Value\":7");
    }

    [Test]
    public void ModelAndFragmentSerialize_AgreeOnIgnoredMembers()
    {
        var model = new JsonIgnore200Root
        {
            Value = 7,
            Secret = "private-value",
            NeverIgnored = "kept",
        };
        var context = new StateCodecContext(
            new StateSchemaMetadata("jsonignore-200-root", 1)
        );

        var modelBuffer = new ArrayBufferWriter<byte>();
        new JsonStateCodec<JsonIgnore200Root>().Serialize(model, modelBuffer, in context);
        var modelJson = Encoding.UTF8.GetString(modelBuffer.WrittenSpan);
        modelJson.ShouldNotContain("private-value");

        var fragmentJson = SerializeTyped(ToFragment(model));
        fragmentJson.ShouldNotContain("private-value");
        fragmentJson.ShouldContain("\"Value\":7");
    }

    [Test]
    public void Deserialize_SkipsIgnoredInputAndPreservesSparseSemantics()
    {
        var codec = new JsonStateCodec<JsonIgnore200Root.Fragment>();
        var sequence = new ReadOnlySequence<byte>(
            Encoding.UTF8.GetBytes(
                "{\"Value\":7,\"Secret\":\"injected\",\"NeverIgnored\":\"kept\",\"Tags\":[1]}"
            )
        );

        var decoded = codec.Deserialize(in sequence, default)!;
        decoded.Secret.IsPresent.ShouldBeFalse();
        decoded.Value.Value.ShouldBe(7);
        decoded.NeverIgnored.Value.ShouldBe("kept");
        decoded.Tags.Value.ShouldBe(new List<int> { 1 });

        // Unspecified members keep sparse semantics: absent stays missing.
        var sparse = codec.Deserialize(
            new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes("{\"Value\":7}")),
            default
        )!;
        sparse.NeverIgnored.IsPresent.ShouldBeFalse();
        sparse.Secret.IsPresent.ShouldBeFalse();

        // Nested ignored members are skipped as well.
        var nested = codec.Deserialize(
            new ReadOnlySequence<byte>(
                Encoding.UTF8.GetBytes("{\"Child\":{\"Count\":1,\"InnerSecret\":\"x\"}}")
            ),
            default
        )!;
        nested.Child.IsPresent.ShouldBeTrue();
        nested.Child.Value!.Count.Value.ShouldBe(1);
        nested.Child.Value.InnerSecret.IsPresent.ShouldBeFalse();
    }

    [Test]
    public void Deserialize_StrictModeAcceptsIgnoredButRejectsUnknown()
    {
        var strict = new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        strict.Converters.Add(new JsonIgnore200Root.Fragment.FragmentJsonConverter());

        // Mirrors System.Text.Json: ignored names do not trip Disallow.
        var decoded = JsonSerializer.Deserialize<JsonIgnore200Root.Fragment>(
            "{\"Value\":7,\"Secret\":\"x\",\"ExplicitSecret\":\"y\"}",
            strict
        )!;
        decoded.Value.Value.ShouldBe(7);
        decoded.Secret.IsPresent.ShouldBeFalse();

        Should.Throw<JsonException>(() =>
            JsonSerializer.Deserialize<JsonIgnore200Root.Fragment>(
                "{\"Value\":7,\"Bogus\":1}",
                strict
            )
        );
    }

    [Test]
    public async Task Deserialize_TypedAndDynamicAsync_SkipIgnored()
    {
        foreach (var useAsync in new[] { false, true })
        {
            var typed = new JsonStateCodec<JsonIgnore200Root.Fragment>();
            var json = "{\"Value\":7,\"Secret\":\"x\"}";
            JsonIgnore200Root.Fragment? value;
            if (!useAsync)
            {
                value = typed.Deserialize(
                    new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json)),
                    default
                );
            }
            else
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
                var pipe = PipeReader.Create(stream);
                try
                {
                    value = (await typed.DeserializeAsync(pipe, default, null)).Value;
                }
                finally
                {
                    await pipe.CompleteAsync();
                }
            }

            value!.Value.Value.ShouldBe(7);
            value.Secret.IsPresent.ShouldBeFalse();
        }

        var dynamic = new JsonStateCodec();
        var decoded = (JsonIgnore200Root.Fragment)dynamic.Deserialize(
            typeof(JsonIgnore200Root.Fragment),
            new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes("{\"Value\":7,\"Secret\":\"x\"}")),
            default
        )!;
        decoded.Value.Value.ShouldBe(7);
        decoded.Secret.IsPresent.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public void ConditionalIgnore_OmitsNullOrDefaultButKeepsMissingPresent()
    {
        // Present null/default values are omitted; missing stays missing.
        var omitted = ToFragment(
            new JsonIgnore200Root
            {
                Value = 1,
                MaybeNull = null,
                MaybeDefault = 0,
            }
        );
        // Force presence of the null/default values.
        omitted = new JsonIgnore200Root.Fragment
        {
            Value = omitted.Value,
            MaybeNull = Optional<string?>.Present(null),
            MaybeDefault = Optional<int>.Present(0),
        };
        var omittedJson = SerializeTyped(omitted);
        omittedJson.ShouldNotContain("MaybeNull");
        omittedJson.ShouldNotContain("MaybeDefault");

        var readBack = new JsonStateCodec<JsonIgnore200Root.Fragment>().Deserialize(
            new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(omittedJson)),
            default
        )!;
        readBack.MaybeNull.IsPresent.ShouldBeFalse();
        readBack.MaybeDefault.IsPresent.ShouldBeFalse();

        // Present non-default values are still written and read back.
        var kept = ToFragment(
            new JsonIgnore200Root { Value = 1, MaybeNull = "v", MaybeDefault = 5 }
        );
        var keptJson = SerializeTyped(kept);
        keptJson.ShouldContain("MaybeNull");
        keptJson.ShouldContain("MaybeDefault");
        var keptBack = new JsonStateCodec<JsonIgnore200Root.Fragment>().Deserialize(
            new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(keptJson)),
            default
        )!;
        keptBack.MaybeNull.Value.ShouldBe("v");
        keptBack.MaybeDefault.Value.ShouldBe(5);
    }

    [Test]
    public void FragmentAlgebraAndPatch_StillSeeIgnoredMembers()
    {
        var model = new JsonIgnore200Root { Value = 7, Secret = "s" };
        var fragment = ToFragment(model);
        fragment.Secret.IsPresent.ShouldBeTrue();
        fragment.ToModel().Secret.ShouldBe("s");

        var patch = new JsonIgnore200Root.Patch { Secret = "patched" };
        var applied = fragment.Apply(patch);
        applied.Secret.Value.ShouldBe("patched");
        applied.ToModel().Secret.ShouldBe("patched");
    }
}
