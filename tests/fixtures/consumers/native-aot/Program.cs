using System.Buffers;
using System.Text.Json;
using Configlue;
using Configlue.Extensibility;
using Configlue.Provider.Json;
using Configlue.Provider.MessagePack;
using Configlue.Resources;
using Configlue.Sources;
using Configlue.State;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using NativeAotConsumer;

var directory = Path.Combine(
    Path.GetTempPath(),
    "configlue-native-aot-" + Guid.NewGuid().ToString("N")
);
Directory.CreateDirectory(directory);
var settingsPath = Path.Combine(directory, "settings.json");

try
{
    using var resource = new FileResource(settingsPath);
    var modelSource = SerializedStateSource.FromResource<SampleAotSetting>(
        "settings",
        resource,
        new JsonStateCodec<SampleAotSetting>(SampleAotJsonContext.Default.SampleAotSetting),
        physicalOrigin: settingsPath
    );
    var settingsSource = StateSourceProjection.Project(
        modelSource,
        static settings => SampleAotSetting.Fragment.From(settings),
        static fragment => fragment.ToModel(),
        projectedSchema: SampleAotSetting.ConfiglueSchema.ToMetadata()
    );

    var firstRead = await settingsSource.Reader.ReadAsync(ConfiglueResourceContext.Default);
    var current = firstRead.Status switch
    {
        StateReadStatus.Success => firstRead.Value?.ToModel()
            ?? throw new InvalidOperationException("The settings source returned a null value."),
        StateReadStatus.NotFound => new SampleAotSetting(),
        _ => throw new IOException("The settings source is temporarily unavailable."),
    };
    Require(current.Name == "World", $"Unexpected default name '{current.Name}'.");

    current.Name = "NativeAOT";
    current.RunCount++;
    var writer =
        settingsSource.Writer
        ?? throw new InvalidOperationException("The settings source is read-only.");
    await writer.WriteAsync(
        ConfiglueResourceContext.Default,
        new StateWriteRequest<SampleAotSetting.Fragment>(SampleAotSetting.Fragment.From(current))
    );

    var updatedRead = await settingsSource.Reader.ReadAsync(ConfiglueResourceContext.Default);
    var updated = updatedRead.Status switch
    {
        StateReadStatus.Success => updatedRead.Value?.ToModel()
            ?? throw new InvalidOperationException("The settings source returned a null value."),
        _ => throw new IOException("The saved settings could not be read."),
    };
    Require(
        updated.Name == "NativeAOT",
        $"Expected persisted name 'NativeAOT', got '{updated.Name}'."
    );
    Require(updated.RunCount == 1, $"Expected persisted run count 1, got '{updated.RunCount}'.");

    await RunGeneratedFragmentFacade(Path.Combine(directory, "facade-settings.json"));
    await RunGeneratedFragmentMessagePack(Path.Combine(directory, "facade-settings.msgpack"));

    Console.WriteLine("CONFIGLUE_NATIVE_AOT_PASS");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
finally
{
    try
    {
        Directory.Delete(directory, recursive: true);
    }
    catch (IOException) { }
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task RunGeneratedFragmentFacade(string settingsPath)
{
    var missingResolverWasRejected = false;
    try
    {
        _ = ConfiglueApp.CreateContext(builder =>
            builder.Add<SampleAotSetting>(model =>
                model.Sources(sources =>
                    sources.FromJsonFile(
                        new JsonFileSourceOptions { Path = settingsPath, WatchChanges = false }
                    )
                )
            )
        );
    }
    catch (InvalidOperationException exception)
    {
        missingResolverWasRejected = exception.Message.Contains(
            "JsonSerializerOptions.TypeInfoResolver",
            StringComparison.Ordinal
        );
    }
    Require(
        missingResolverWasRejected,
        "The JSON facade accepted missing source-generated metadata."
    );

    var options = new JsonSerializerOptions { TypeInfoResolver = SampleAotJsonContext.Default };
    await using var context = ConfiglueApp.CreateContext(builder =>
        builder.Add<SampleAotSetting>(model =>
            model.Sources(sources =>
                sources.FromJsonFile(
                    new JsonFileSourceOptions
                    {
                        Path = settingsPath,
                        WatchChanges = false,
                        SerializerOptions = options,
                    }
                )
            )
        )
    );

    var state = context.GetState<SampleAotSetting>();
    var current = await state.GetValueAsync();
    Require(current.Name == "World", $"Unexpected facade default name '{current.Name}'.");
    await state.SaveAsync(settings =>
    {
        settings.Name = "NativeAOT facade";
        settings.RunCount = current.RunCount + 1;
    });

    var updated = await state.GetValueAsync();
    Require(
        updated.Name == "NativeAOT facade",
        $"Expected facade value 'NativeAOT facade', got '{updated.Name}'."
    );
    Require(updated.RunCount == 1, $"Expected facade run count 1, got '{updated.RunCount}'.");
}

static async Task RunGeneratedFragmentMessagePack(string settingsPath)
{
    var serializerOptions = new MessagePackSerializerOptions(
        new ConfiglueMessagePackResolver(new NativeAotMessagePackResolver())
    );

    var directPoco = new SampleAotPoco { Name = "generated resolver", Value = 17 };
    var directBytes = MessagePackSerializer.Serialize(directPoco, serializerOptions);
    var directRoundTrip = MessagePackSerializer.Deserialize<SampleAotPoco>(
        directBytes,
        serializerOptions
    );
    Require(
        directRoundTrip is { Name: "generated resolver", Value: 17 },
        "The generated MessagePack resolver did not round-trip the custom POCO."
    );
    var listRoundTrip = MessagePackSerializer.Deserialize<List<SampleAotPoco>>(
        MessagePackSerializer.Serialize(new List<SampleAotPoco> { directPoco }, serializerOptions),
        serializerOptions
    );
    Require(
        listRoundTrip is { Count: 1 } && listRoundTrip[0].Value == 17,
        "The AOT collection formatter did not round-trip the custom POCO collection."
    );

    var sourceModel = new SampleAotSetting
    {
        Endpoint = new SampleAotPoco { Name = "primary", Value = 31 },
        Endpoints = new List<SampleAotPoco>
        {
            new() { Name = "first", Value = 47 },
            new() { Name = "second", Value = 59 },
        },
    };
    var fragmentCodec = new MessagePackStateCodec<SampleAotSetting.Fragment>(serializerOptions);
    var fragmentBuffer = new ArrayBufferWriter<byte>();
    fragmentCodec.Serialize(SampleAotSetting.Fragment.From(sourceModel), fragmentBuffer, default);
    var fragment = fragmentCodec.Deserialize(
        new ReadOnlySequence<byte>(fragmentBuffer.WrittenMemory),
        default
    );
    var fragmentRoundTrip =
        fragment?.ToModel()
        ?? throw new InvalidOperationException("The generated MessagePack fragment was null.");
    Require(
        fragmentRoundTrip.Endpoint is { Name: "primary", Value: 31 },
        "The generated fragment lost the custom POCO member."
    );
    Require(
        fragmentRoundTrip.Endpoints.Count == 2
            && fragmentRoundTrip.Endpoints[0] is { Name: "first", Value: 47 }
            && fragmentRoundTrip.Endpoints[1] is { Name: "second", Value: 59 },
        "The generated fragment lost the custom POCO collection member."
    );

    await using (
        var context = ConfiglueApp.CreateContext(builder =>
            builder.Add<SampleAotSetting>(model =>
                model.Sources(sources =>
                    sources.FromGeneratedMessagePackFile(
                        new MessagePackFileSourceOptions
                        {
                            Path = settingsPath,
                            WatchChanges = false,
                            SerializerOptions = serializerOptions,
                        },
                        serializerOptions
                    )
                )
            )
        )
    )
    {
        var state = context.GetState<SampleAotSetting>();
        _ = await state.GetValueAsync();
        await state.SaveAsync(patch =>
        {
            patch.Numbers = new List<int> { 17, 23, 42 };
        });

        var updated = await state.GetValueAsync();
        Require(updated.Numbers.Count == 3, "MessagePack facade lost its collection member.");
        Require(
            updated.Numbers.SequenceEqual([17, 23, 42]),
            "MessagePack facade changed the collection values."
        );
    }

    await using var rereadContext = ConfiglueApp.CreateContext(builder =>
        builder.Add<SampleAotSetting>(model =>
            model.Sources(sources =>
                sources.FromGeneratedMessagePackFile(
                    new MessagePackFileSourceOptions
                    {
                        Path = settingsPath,
                        WatchChanges = false,
                        SerializerOptions = serializerOptions,
                    },
                    serializerOptions
                )
            )
        )
    );
    var persisted = await rereadContext.GetState<SampleAotSetting>().GetValueAsync();
    Require(
        persisted.Numbers.SequenceEqual([17, 23, 42]),
        "MessagePack facade did not persist the collection values."
    );
}

internal sealed class NativeAotMessagePackResolver : IFormatterResolver
{
    private static readonly IMessagePackFormatter<List<SampleAotPoco>> PocoCollectionFormatter =
        new ListFormatter<SampleAotPoco>()!;
    private static readonly IMessagePackFormatter<List<int>> NumberCollectionFormatter =
        new ListFormatter<int>()!;
    private static readonly IMessagePackFormatter<string?> StringValueFormatter =
        new AotStringFormatter();
    private static readonly IMessagePackFormatter<int> IntegerValueFormatter =
        new AotInt32Formatter();

    public IMessagePackFormatter<T>? GetFormatter<T>() =>
        typeof(T) == typeof(List<SampleAotPoco>)
            ? (IMessagePackFormatter<T>)(object)PocoCollectionFormatter
        : typeof(T) == typeof(List<int>)
            ? (IMessagePackFormatter<T>)(object)NumberCollectionFormatter
        : typeof(T) == typeof(string) ? (IMessagePackFormatter<T>)(object)StringValueFormatter
        : typeof(T) == typeof(int) ? (IMessagePackFormatter<T>)(object)IntegerValueFormatter
        : SampleMessagePackResolver.Instance.GetFormatter<T>();

    internal sealed class AotStringFormatter : IMessagePackFormatter<string?>
    {
        public string? Deserialize(
            ref MessagePackReader reader,
            MessagePackSerializerOptions options
        ) => reader.ReadString()!;

        public void Serialize(
            ref MessagePackWriter writer,
            string? value,
            MessagePackSerializerOptions options
        ) => writer.Write(value);
    }

    internal sealed class AotInt32Formatter : IMessagePackFormatter<int>
    {
        public int Deserialize(
            ref MessagePackReader reader,
            MessagePackSerializerOptions options
        ) => reader.ReadInt32();

        public void Serialize(
            ref MessagePackWriter writer,
            int value,
            MessagePackSerializerOptions options
        ) => writer.Write(value);
    }
}
