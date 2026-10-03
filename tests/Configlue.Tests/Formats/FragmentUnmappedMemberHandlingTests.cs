using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Configlue.Provider.Json;

namespace Configlue.Tests;

[ConfiglueModel("unmapped-197-root", Version = 1)]
public partial class Unmapped197RootSettings
{
    public int Value { get; set; }
}

[ConfiglueModel("unmapped-197-child", Version = 1)]
public partial class Unmapped197ChildSettings
{
    public int Count { get; set; }
}

[ConfiglueModel("unmapped-197-parent", Version = 1)]
public partial class Unmapped197ParentSettings
{
    public string? Label { get; set; }

    public Unmapped197ChildSettings? Child { get; set; }
}

[ConfiglueModel("unmapped-197-empty", Version = 1)]
public partial class Unmapped197EmptySettings { }

public sealed class FragmentUnmappedMemberHandlingTests
{
    [Test]
    public void Skip_IgnoresUnknownMembers()
    {
        var @default = new JsonSerializerOptions();
        @default.Converters.Add(new Unmapped197RootSettings.Fragment.FragmentJsonConverter());
        JsonSerializer
            .Deserialize<Unmapped197RootSettings.Fragment>(
                "{\"Value\":7,\"Vlaue\":99}",
                @default
            )!
            .Value.Value.ShouldBe(7);

        var skip = new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        };
        skip.Converters.Add(new Unmapped197RootSettings.Fragment.FragmentJsonConverter());
        JsonSerializer
            .Deserialize<Unmapped197RootSettings.Fragment>("{\"Value\":7,\"Vlaue\":99}", skip)!
            .Value.Value.ShouldBe(7);

        var empty = new JsonSerializerOptions();
        empty.Converters.Add(new Unmapped197EmptySettings.Fragment.FragmentJsonConverter());
        JsonSerializer
            .Deserialize<Unmapped197EmptySettings.Fragment>(
                "{\"unknown\":{\"nested\":[1,2]}}",
                empty
            )!
            .IsEmpty.ShouldBeTrue();
    }

    [Test]
    public void Disallow_RejectsUnknownRootMember()
    {
        var options = new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new Unmapped197RootSettings.Fragment.FragmentJsonConverter());

        var exception = Should.Throw<JsonException>(() =>
            JsonSerializer.Deserialize<Unmapped197RootSettings.Fragment>(
                "{\"Value\":7,\"Vlaue\":99}",
                options
            )
        );
        exception.Message.ShouldContain("Vlaue");
        exception.Message.ShouldContain("Fragment");

        JsonSerializer
            .Deserialize<Unmapped197RootSettings.Fragment>("{\"Value\":7}", options)!
            .Value.Value.ShouldBe(7);
    }

    [Test]
    public void Disallow_RejectsUnknownMemberForEmptyModel()
    {
        var options = new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new Unmapped197EmptySettings.Fragment.FragmentJsonConverter());

        var exception = Should.Throw<JsonException>(() =>
            JsonSerializer.Deserialize<Unmapped197EmptySettings.Fragment>(
                "{\"unknown\":{\"nested\":[1,2]}}",
                options
            )
        );
        exception.Message.ShouldContain("unknown");
        exception.Message.ShouldContain("Fragment");

        JsonSerializer
            .Deserialize<Unmapped197EmptySettings.Fragment>("{}", options)!
            .IsEmpty.ShouldBeTrue();
    }

    [Test]
    public void Disallow_RejectsUnknownNestedMember()
    {
        var options = new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new Unmapped197ParentSettings.Fragment.FragmentJsonConverter());

        var nestedException = Should.Throw<JsonException>(() =>
            JsonSerializer.Deserialize<Unmapped197ParentSettings.Fragment>(
                "{\"Label\":\"a\",\"Child\":{\"Count\":1,\"Bogus\":2}}",
                options
            )
        );
        nestedException.Message.ShouldContain("Bogus");

        var rootException = Should.Throw<JsonException>(() =>
            JsonSerializer.Deserialize<Unmapped197ParentSettings.Fragment>(
                "{\"Label\":\"a\",\"Child\":{\"Count\":1},\"Extra\":true}",
                options
            )
        );
        rootException.Message.ShouldContain("Extra");

        var decoded = JsonSerializer.Deserialize<Unmapped197ParentSettings.Fragment>(
            "{\"Label\":\"a\",\"Child\":{\"Count\":1}}",
            options
        )!;
        decoded.Label.Value.ShouldBe("a");
        decoded.Child.Value!.Count.Value.ShouldBe(1);
    }

    [Test]
    public void Disallow_TypedCodecCombinedDecodeIsRecoverable()
    {
        var codec = new JsonStateCodec<Unmapped197RootSettings.Fragment>(
            new JsonSerializerOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            }
        );
        var sequence = new ReadOnlySequence<byte>(
            Encoding.UTF8.GetBytes("{\"Value\":7,\"Vlaue\":99}")
        );

        var exception = Should.Throw<JsonException>(() =>
            codec.DeserializeWithMetadata(in sequence, default)
        );
        exception.Message.ShouldContain("Vlaue");
        codec.IsRecoverableReadException(exception).ShouldBeTrue();

        var valid = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes("{\"Value\":7}"));
        codec.DeserializeWithMetadata(in valid, default).Value!.Value.Value.ShouldBe(7);
        codec.Deserialize(in valid, default)!.Value.Value.ShouldBe(7);
    }

    [Test]
    public async Task Disallow_TypedCodecAsyncDecode()
    {
        var strict = new JsonStateCodec<Unmapped197RootSettings.Fragment>(
            new JsonSerializerOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            }
        );
        var strictException = await Should.ThrowAsync<JsonException>(async () =>
            await ReadFromPipeAsync(strict, "{\"Value\":7,\"Vlaue\":99}")
        );
        strict.IsRecoverableReadException(strictException).ShouldBeTrue();

        var valid = await ReadFromPipeAsync(strict, "{\"Value\":7}");
        valid.Status.ShouldBe(StateReadStatus.Success);
        valid.Value!.Value.Value.ShouldBe(7);

        var lenient = new JsonStateCodec<Unmapped197RootSettings.Fragment>();
        var skipped = await ReadFromPipeAsync(lenient, "{\"Value\":7,\"Vlaue\":99}");
        skipped.Status.ShouldBe(StateReadStatus.Success);
        skipped.Value!.Value.Value.ShouldBe(7);
    }

    [Test]
    public void Disallow_DynamicCodecRejectsUnknownMember()
    {
        var strict = new JsonStateCodec(
            new JsonSerializerOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            }
        );
        var sequence = new ReadOnlySequence<byte>(
            Encoding.UTF8.GetBytes("{\"Value\":7,\"Vlaue\":99}")
        );

        var exception = Should.Throw<JsonException>(() =>
            strict.Deserialize(
                typeof(Unmapped197RootSettings.Fragment),
                in sequence,
                default
            )
        );
        exception.Message.ShouldContain("Vlaue");
        strict.IsRecoverableReadException(exception).ShouldBeTrue();

        var lenient = new JsonStateCodec();
        var decoded = (Unmapped197RootSettings.Fragment)lenient.Deserialize(
            typeof(Unmapped197RootSettings.Fragment),
            in sequence,
            default
        )!;
        decoded.Value.Value.ShouldBe(7);
    }

    [Test]
    public void Disallow_HonorsNamingPolicyAndCaseSensitivity()
    {
        var camel = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        camel.Converters.Add(new Unmapped197RootSettings.Fragment.FragmentJsonConverter());

        JsonSerializer
            .Deserialize<Unmapped197RootSettings.Fragment>("{\"value\":7}", camel)!
            .Value.Value.ShouldBe(7);

        var camelException = Should.Throw<JsonException>(() =>
            JsonSerializer.Deserialize<Unmapped197RootSettings.Fragment>(
                "{\"value\":7,\"vlaue\":99}",
                camel
            )
        );
        camelException.Message.ShouldContain("vlaue");

        var insensitive = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        insensitive.Converters.Add(
            new Unmapped197RootSettings.Fragment.FragmentJsonConverter()
        );
        JsonSerializer
            .Deserialize<Unmapped197RootSettings.Fragment>("{\"VALUE\":7}", insensitive)!
            .Value.Value.ShouldBe(7);
        var caseException = Should.Throw<JsonException>(() =>
            JsonSerializer.Deserialize<Unmapped197RootSettings.Fragment>(
                "{\"VALUE\":7,\"VLAUE\":99}",
                insensitive
            )
        );
        caseException.Message.ShouldContain("VLAUE");
    }

    [Test]
    public void Disallow_SimpleLayoutVersionAndSchemaAreExcluded()
    {
        var codec = new JsonStateCodec<Unmapped197RootSettings.Fragment>(
            new JsonSerializerOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            }
        );

        var versioned = new ReadOnlySequence<byte>(
            Encoding.UTF8.GetBytes("{\"$version\":1,\"Value\":7}")
        );
        codec.DeserializeWithMetadata(in versioned, default).Value!.Value.Value.ShouldBe(7);
        codec.Deserialize(in versioned, default)!.Value.Value.ShouldBe(7);

        var schematized = new ReadOnlySequence<byte>(
            Encoding.UTF8.GetBytes("{\"$version\":1,\"$schema\":\"https://example.test/s.json\",\"Value\":7}")
        );
        codec.DeserializeWithMetadata(in schematized, default).Value!.Value.Value.ShouldBe(7);

        var unknown = new ReadOnlySequence<byte>(
            Encoding.UTF8.GetBytes("{\"$version\":1,\"Value\":7,\"Vlaue\":99}")
        );
        Should.Throw<JsonException>(() => codec.DeserializeWithMetadata(in unknown, default));
        Should.Throw<JsonException>(() => codec.Deserialize(in unknown, default));
    }

    private static async Task<StateReadResult<T>> ReadFromPipeAsync<T>(
        JsonStateCodec<T> codec,
        string json
    )
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var pipe = PipeReader.Create(stream);
        try
        {
            return await codec.DeserializeAsync(pipe, default, null);
        }
        finally
        {
            await pipe.CompleteAsync();
        }
    }
}
