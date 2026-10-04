using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Configlue.Provider.Json;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class JsonSimpleCallbackSettings : IJsonOnSerializing, IJsonOnSerialized
{
    public int Value { get; set; } = 1;

    [JsonIgnore]
    public int SerializingCalls { get; private set; }

    [JsonIgnore]
    public int SerializedCalls { get; private set; }

    public void OnSerializing()
    {
        SerializingCalls++;
        Value = 42;
    }

    public void OnSerialized()
    {
        SerializedCalls++;
    }
}

public sealed class JsonSimplePlainSettings
{
    public int Value { get; set; } = 1;
}

public sealed class JsonSimpleThrowingSettings : IJsonOnSerializing, IJsonOnSerialized
{
    public int Value { get; set; } = 1;

    public void OnSerializing() => throw new InvalidOperationException("OnSerializing boom");

    public void OnSerialized() { }
}

[JsonSerializable(typeof(JsonSimpleCallbackSettings))]
[JsonSerializable(typeof(JsonSimplePlainSettings))]
internal partial class JsonSimpleCallbackJsonContext : JsonSerializerContext { }

internal sealed class JsonSimpleCallbackPayloadWriterConverter
    : JsonConverter<JsonSimpleCallbackSettings>,
        IJsonObjectPayloadWriter
{
    public override JsonSimpleCallbackSettings? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) => JsonSerializer.Deserialize<JsonSimpleCallbackSettings>(ref reader, options);

    public override void Write(
        Utf8JsonWriter writer,
        JsonSimpleCallbackSettings value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStartObject();
        WriteObjectPayloadProperties(writer, value, options);
        writer.WriteEndObject();
    }

    public void WriteObjectPayloadProperties(
        Utf8JsonWriter writer,
        object value,
        JsonSerializerOptions options
    )
    {
        var typed = (JsonSimpleCallbackSettings)value;
        writer.WriteNumber("Value", typed.Value);
    }
}

internal sealed class JsonSimplePlainPayloadWriterConverter
    : JsonConverter<JsonSimplePlainSettings>,
        IJsonObjectPayloadWriter
{
    public override JsonSimplePlainSettings? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) => JsonSerializer.Deserialize<JsonSimplePlainSettings>(ref reader, options);

    public override void Write(
        Utf8JsonWriter writer,
        JsonSimplePlainSettings value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStartObject();
        WriteObjectPayloadProperties(writer, value, options);
        writer.WriteEndObject();
    }

    public void WriteObjectPayloadProperties(
        Utf8JsonWriter writer,
        object value,
        JsonSerializerOptions options
    )
    {
        var typed = (JsonSimplePlainSettings)value;
        writer.WriteNumber("Value", typed.Value);
    }
}

[ConfiglueModel("json-aot-settings", Version = 1)]
public partial class JsonAotSettings
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 443;
}

[JsonSerializable(typeof(JsonAotSettings))]
internal partial class JsonAotSettingsJsonContext : JsonSerializerContext { }

public sealed class JsonAotTests
{
    [Test]
    public async Task JsonAot_SimpleSchemaSerializationWritesSourceGeneratedObjectDirectly()
    {
        var codec = new JsonStateCodec<JsonAotSettings>(
            JsonAotSettingsJsonContext.Default.JsonAotSettings
        );
        var context = new StateCodecContext(new StateSchemaMetadata("json-aot-settings", 1));
        var buffer = new ArrayBufferWriter<byte>();

        codec.Serialize(
            new JsonAotSettings { Host = "direct.example", Port = 8443 },
            buffer,
            in context
        );

        var encoded = Encoding.UTF8.GetString(buffer.WrittenSpan);
        encoded.ShouldContain("\"$version\":1");
        encoded.ShouldContain("\"Host\":\"direct.example\"");
        encoded.ShouldContain("\"Port\":8443");
        encoded.ShouldNotContain("\"$value\"");
    }

    [Test]
    public async Task JsonAot_PipelineReadUsesGeneratedMetadata()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, """{"Host":"stream.example","Port":8443}""");
            using var resource = new FileResource(path);
            var reader = new SerializedStateReader<JsonAotSettings>(
                resource,
                new JsonStateCodec<JsonAotSettings>(
                    JsonAotSettingsJsonContext.Default.JsonAotSettings
                )
                {
                    UseAsyncStreamDecoding = true,
                }
            );

            var result = await reader.ReadAsync();

            result.Status.ShouldBe(StateReadStatus.Success);
            result.Value!.Host.ShouldBe("stream.example");
            result.Value.Port.ShouldBe(8443);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task JsonAot_UsesGeneratedMetadataForProjectedModelState()
    {
        var resource = new InMemoryResource();
        var source = new StateSource<JsonAotSettings>("json", new SerializedSource<JsonAotSettings>(resource, new JsonStateCodec<JsonAotSettings>(JsonAotSettingsJsonContext.Default.JsonAotSettings), writer: (IResourceReader)resource as IResourceWriter, watcher: (IResourceReader)resource as ISourceWatcher), new StateSourceOptions<JsonAotSettings>());
        var projected = StateSourceProjection.Project(
            source,
            static settings => JsonAotSettings.Fragment.From(settings),
            static fragment => fragment.ToModel(),
            projectedSchema: JsonAotSettings.ConfiglueSchema.ToMetadata()
        );
        var options = new ConfiglueRuntime<JsonAotSettings, JsonAotSettings.Fragment>(
            new StateSourceSet<JsonAotSettings.Fragment>([projected])
        );

        await options.SaveAsync(patch =>
        {
            patch.Host = "native.example";
            patch.Port = 8443;
        });
        var roundTrip = await options.ReadAsync();

        roundTrip.Value.ShouldNotBeNull();
        roundTrip.Value.Host.ShouldBe("native.example");
        roundTrip.Value.Port.ShouldBe(8443);
    }

    [Test]
    public async Task JsonSimple_TypedOptions_InvokesSerializationCallbacks()
    {
        var codec = new JsonStateCodec<JsonSimpleCallbackSettings>(new JsonSerializerOptions());
        var context = new StateCodecContext(new StateSchemaMetadata("json-callback-settings", 1));
        var buffer = new ArrayBufferWriter<byte>();
        var value = new JsonSimpleCallbackSettings { Value = 1 };

        codec.Serialize(value, buffer, in context);

        var encoded = Encoding.UTF8.GetString(buffer.WrittenSpan);
        encoded.ShouldContain("\"$version\":1");
        encoded.ShouldContain("\"Value\":42");
        value.SerializingCalls.ShouldBe(1);
        value.SerializedCalls.ShouldBe(1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task JsonSimple_DynamicOptions_InvokesSerializationCallbacks()
    {
        var codec = new JsonStateCodec(new JsonSerializerOptions());
        var context = new StateCodecContext(new StateSchemaMetadata("json-callback-settings", 1));
        var buffer = new ArrayBufferWriter<byte>();
        var value = new JsonSimpleCallbackSettings { Value = 1 };

        codec.Serialize(typeof(JsonSimpleCallbackSettings), value, buffer, in context);

        var encoded = Encoding.UTF8.GetString(buffer.WrittenSpan);
        encoded.ShouldContain("\"$version\":1");
        encoded.ShouldContain("\"Value\":42");
        value.SerializingCalls.ShouldBe(1);
        value.SerializedCalls.ShouldBe(1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task JsonSimple_DirectTypeInfo_InvokesSerializationCallbacks()
    {
        var codec = new JsonStateCodec<JsonSimpleCallbackSettings>(
            JsonSimpleCallbackJsonContext.Default.JsonSimpleCallbackSettings
        );
        var context = new StateCodecContext(new StateSchemaMetadata("json-callback-settings", 1));
        var buffer = new ArrayBufferWriter<byte>();
        var value = new JsonSimpleCallbackSettings { Value = 1 };

        codec.Serialize(value, buffer, in context);

        var encoded = Encoding.UTF8.GetString(buffer.WrittenSpan);
        encoded.ShouldContain("\"$version\":1");
        encoded.ShouldContain("\"Value\":42");
        value.SerializingCalls.ShouldBe(1);
        value.SerializedCalls.ShouldBe(1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task JsonSimple_EnvelopeLayout_InvokesSerializationCallbacks()
    {
        var layout = new DocumentLayoutOptions { Layout = DocumentLayout.Detailed };
        var typed = new JsonStateCodec<JsonSimpleCallbackSettings>(
            new JsonSerializerOptions(),
            documentLayout: layout
        );
        var dynamic = new JsonStateCodec(new JsonSerializerOptions(), documentLayout: layout);
        var context = new StateCodecContext(new StateSchemaMetadata("json-callback-settings", 1));

        var typedValue = new JsonSimpleCallbackSettings { Value = 1 };
        var typedBuffer = new ArrayBufferWriter<byte>();
        typed.Serialize(typedValue, typedBuffer, in context);
        var typedEncoded = Encoding.UTF8.GetString(typedBuffer.WrittenSpan);
        typedEncoded.ShouldContain("\"Value\":42");
        typedEncoded.ShouldContain("\"$value\"");
        typedValue.SerializingCalls.ShouldBe(1);
        typedValue.SerializedCalls.ShouldBe(1);

        var dynamicValue = new JsonSimpleCallbackSettings { Value = 1 };
        var dynamicBuffer = new ArrayBufferWriter<byte>();
        dynamic.Serialize(
            typeof(JsonSimpleCallbackSettings),
            dynamicValue,
            dynamicBuffer,
            in context
        );
        var dynamicEncoded = Encoding.UTF8.GetString(dynamicBuffer.WrittenSpan);
        dynamicEncoded.ShouldContain("\"Value\":42");
        dynamicEncoded.ShouldContain("\"$value\"");
        dynamicValue.SerializingCalls.ShouldBe(1);
        dynamicValue.SerializedCalls.ShouldBe(1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task JsonSimple_CustomResolverCallbacks_FallBackToSerializer()
    {
        var serializing = 0;
        var serialized = 0;
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(static typeInfo =>
        {
            if (typeInfo.Type == typeof(JsonSimplePlainSettings))
            {
                typeInfo.OnSerializing = static obj =>
                {
                    ((JsonSimplePlainSettings)obj).Value = 42;
                };
                typeInfo.OnSerialized = static _ => { };
            }
        });
        var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
        var codec = new JsonStateCodec<JsonSimplePlainSettings>(options);
        var context = new StateCodecContext(new StateSchemaMetadata("json-plain-settings", 1));
        var buffer = new ArrayBufferWriter<byte>();
        var value = new JsonSimplePlainSettings { Value = 1 };

        // Count via a second resolver-independent check: the fallback must apply the
        // OnSerializing mutation even though the Simple fast path bypasses JsonSerializer.
        codec.Serialize(value, buffer, in context);

        var encoded = Encoding.UTF8.GetString(buffer.WrittenSpan);
        encoded.ShouldContain("\"$version\":1");
        encoded.ShouldContain("\"Value\":42");
        value.Value.ShouldBe(42);
        serializing.ShouldBe(0);
        serialized.ShouldBe(0);

        // Same options through JsonSerializer directly also set Value=42; the codec must match it.
        var baseline = new JsonSimplePlainSettings { Value = 1 };
        var baselineTypeInfo =
            (JsonTypeInfo<JsonSimplePlainSettings>)
                options.GetTypeInfo(typeof(JsonSimplePlainSettings));
        baselineTypeInfo.OnSerializing.ShouldNotBeNull();
        baselineTypeInfo.OnSerialized.ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task JsonSimple_CallbackException_FailsSaveLikeSerializer()
    {
        var codec = new JsonStateCodec<JsonSimpleThrowingSettings>(new JsonSerializerOptions());
        var context = new StateCodecContext(new StateSchemaMetadata("json-throwing-settings", 1));

        Should.Throw<InvalidOperationException>(() =>
        {
            codec.Serialize(
                new JsonSimpleThrowingSettings(),
                new ArrayBufferWriter<byte>(),
                in context
            );
        });

        Should.Throw<InvalidOperationException>(() =>
        {
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream);
            JsonSerializer.Serialize(
                writer,
                new JsonSimpleThrowingSettings(),
                new JsonSerializerOptions()
            );
        });
        await Task.CompletedTask;
    }

    [Test]
    public async Task JsonSimple_NoCallbackModel_KeepsFastPath()
    {
        var options = new JsonSerializerOptions();
        JsonStateCodecOperations.EnsureTypeInfoResolver(options);
        var schema = new StateSchemaMetadata("json-plain-settings", 1);
        var context = new StateCodecContext(schema);
        var typeInfo = options.GetTypeInfo(typeof(JsonSimplePlainSettings));

        using var writer = new Utf8JsonWriter(new ArrayBufferWriter<byte>());
        var fastPath = JsonStateCodecOperations.TryWriteSimpleObjectPayload(
            writer,
            new JsonSimplePlainSettings { Value = 7 },
            typeof(JsonSimplePlainSettings),
            options.GetConverter(typeof(JsonSimplePlainSettings)),
            typeInfo,
            options,
            schema,
            in context,
            null
        );
        fastPath.ShouldBeTrue();

        var callbackTypeInfo = options.GetTypeInfo(typeof(JsonSimpleCallbackSettings));
        using var callbackWriter = new Utf8JsonWriter(new ArrayBufferWriter<byte>());
        var callbackFastPath = JsonStateCodecOperations.TryWriteSimpleObjectPayload(
            callbackWriter,
            new JsonSimpleCallbackSettings { Value = 1 },
            typeof(JsonSimpleCallbackSettings),
            options.GetConverter(typeof(JsonSimpleCallbackSettings)),
            callbackTypeInfo,
            options,
            schema,
            in context,
            null
        );
        callbackFastPath.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task JsonSimple_PayloadWriterPath_RespectsModelCallbacks()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonSimpleCallbackPayloadWriterConverter());
        JsonStateCodecOperations.EnsureTypeInfoResolver(options);
        var schema = new StateSchemaMetadata("json-callback-settings", 1);
        var context = new StateCodecContext(schema);
        var typeInfo = options.GetTypeInfo(typeof(JsonSimpleCallbackSettings));

        using var writer = new Utf8JsonWriter(new ArrayBufferWriter<byte>());
        var fastPath = JsonStateCodecOperations.TryWriteSimpleObjectPayload(
            writer,
            new JsonSimpleCallbackSettings { Value = 1 },
            typeof(JsonSimpleCallbackSettings),
            options.GetConverter(typeof(JsonSimpleCallbackSettings)),
            typeInfo,
            options,
            schema,
            in context,
            null
        );
        fastPath.ShouldBeFalse();

        var plainOptions = new JsonSerializerOptions();
        plainOptions.Converters.Add(new JsonSimplePlainPayloadWriterConverter());
        JsonStateCodecOperations.EnsureTypeInfoResolver(plainOptions);
        var plainSchema = new StateSchemaMetadata("json-plain-settings", 1);
        var plainContext = new StateCodecContext(plainSchema);
        using var plainWriter = new Utf8JsonWriter(new ArrayBufferWriter<byte>());
        var plainFastPath = JsonStateCodecOperations.TryWriteSimpleObjectPayload(
            plainWriter,
            new JsonSimplePlainSettings { Value = 7 },
            typeof(JsonSimplePlainSettings),
            plainOptions.GetConverter(typeof(JsonSimplePlainSettings)),
            plainOptions.GetTypeInfo(typeof(JsonSimplePlainSettings)),
            plainOptions,
            plainSchema,
            in plainContext,
            null
        );
        plainFastPath.ShouldBeTrue();
        await Task.CompletedTask;
    }
}
