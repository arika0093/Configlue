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
using NativeAotSupportedConsumer;

var directory = Path.Combine(
    Path.GetTempPath(),
    "configlue-native-aot-supported-" + Guid.NewGuid().ToString("N")
);
Directory.CreateDirectory(directory);
var settingsPath = Path.Combine(directory, "settings.json");

try
{
    using var resource = new FileResource(settingsPath);
    var serialized = new SerializedSource<SampleAotSetting>(
        resource,
        new JsonStateCodec<SampleAotSetting>(SampleAotJsonContext.Default.SampleAotSetting),
        writer: resource,
        watcher: resource
    );
    var modelSource = new StateSource<SampleAotSetting>(
        "settings",
        serialized,
        new StateSourceOptions<SampleAotSetting> { PhysicalOrigin = settingsPath }
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

    Console.WriteLine("CONFIGLUE_NATIVE_AOT_SUPPORTED_PASS");
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

    var generatedFragment = SampleAotSetting.Fragment.From(
        new SampleAotSetting
        {
            Endpoint = new SampleAotPoco { Name = "endpoint", Value = 23 },
            Endpoints = [new SampleAotPoco { Name = "first", Value = 47 }],
        }
    );
    var generatedConverter = ConfiglueJsonFragmentRegistry<SampleAotSetting.Fragment>.Converter;
    var fragmentBuffer = new ArrayBufferWriter<byte>();
    using (var writer = new Utf8JsonWriter(fragmentBuffer))
    {
        generatedConverter.Write(writer, generatedFragment, options);
    }

    var reader = new Utf8JsonReader(fragmentBuffer.WrittenSpan);
    Require(reader.Read(), "The generated JSON fragment was empty.");
    var decodedFragment =
        generatedConverter.Read(ref reader, typeof(SampleAotSetting.Fragment), options)
        ?? throw new InvalidOperationException(
            "The generated JSON converter returned a null fragment."
        );
    Require(
        decodedFragment.Endpoint.Value?.Name == "endpoint"
            && decodedFragment.Endpoint.Value.Value == 23,
        "The generated fragment converter did not round-trip the custom member type."
    );
    Require(
        decodedFragment.Endpoints.Value is { Count: 1 } endpoints
            && endpoints[0].Name == "first"
            && endpoints[0].Value == 47,
        "The generated fragment converter did not round-trip the collection member type."
    );
}

static MessagePackSerializerOptions CreateSupportedMessagePackOptions() =>
    ConfiglueMessagePackResolver.CreateClosedWorldOptions(
        SampleMessagePackResolver.Instance,
        new SupportedMessagePackLeafResolver()
    );

static async Task RunGeneratedFragmentMessagePack(string settingsPath)
{
    // Supported MessagePack path (#251): generated fragment formatters first, then an
    // explicit closed-world leaf resolver (generated POCO resolver, explicit primitive,
    // per-collection, and enum formatters). No StandardResolver, built-in resolver
    // helper, contractless/dynamic resolver, or GetFormatterDynamic fallback is reachable.
    var serializerOptions = CreateSupportedMessagePackOptions();
    _ = SampleAotSetting.ConfiglueSchema;

    if (
        !ConfiglueMessagePackFragmentRegistry<SampleAotSetting.Fragment>.TryGetFormatter(
            out var formatter
        )
    )
    {
        throw new InvalidOperationException(
            "The generated MessagePack fragment formatter was not registered."
        );
    }

    var initialFragment = SampleAotSetting.Fragment.From(
        new SampleAotSetting
        {
            Endpoint = new SampleAotPoco { Name = "fragment", Value = 23 },
            Endpoints =
            [
                new SampleAotPoco { Name = "first", Value = 47 },
                new SampleAotPoco { Name = "second", Value = 59 },
            ],
            Numbers = [1, 3, 5],
            Label = null,
            RetryLimit = 3,
            Status = SampleAotStatus.Active,
            Child = new SampleAotChild { Name = "nested", Value = 31 },
        }
    );

    // Direct codec round trip over the generated fragment formatter.
    var codec = new MessagePackStateCodec<SampleAotSetting.Fragment>(serializerOptions);
    var directBuffer = new ArrayBufferWriter<byte>();
    codec.Serialize(initialFragment, directBuffer, default);
    var directRead =
        codec.Deserialize(new ReadOnlySequence<byte>(directBuffer.WrittenMemory), default)
        ?? throw new InvalidOperationException("The MessagePack codec returned a null fragment.");
    RequireFragmentCoverage(directRead, "direct");

    // Envelope round trip through the generated MessagePack file source.
    var fragmentWriter = new ArrayBufferWriter<byte>();
    var messagePackWriter = new MessagePackWriter(fragmentWriter);
    messagePackWriter.WriteMapHeader(2);
    messagePackWriter.Write("$configlue");
    messagePackWriter.WriteMapHeader(2);
    messagePackWriter.Write("version");
    messagePackWriter.Write(SampleAotSetting.ConfiglueSchema.Version);
    messagePackWriter.Write("id");
    messagePackWriter.Write(SampleAotSetting.ConfiglueSchema.Id);
    messagePackWriter.Write("$value");
    formatter!.Serialize(ref messagePackWriter, initialFragment, serializerOptions);
    messagePackWriter.Flush();
    await File.WriteAllBytesAsync(settingsPath, fragmentWriter.WrittenMemory.ToArray());

    await using (var readContext = CreateMessagePackContext())
    {
        var readState = readContext.GetState<SampleAotSetting>();
        var initial = await readState.GetValueAsync();
        Require(
            initial.Endpoint is { Name: "fragment", Value: 23 }
                && initial.Endpoints.Count == 2
                && initial.Endpoints[0] is { Name: "first", Value: 47 }
                && initial.Endpoints[1] is { Name: "second", Value: 59 }
                && initial.Numbers.SequenceEqual([1, 3, 5])
                && initial.Label is null
                && initial.RetryLimit == 3
                && initial.Status == SampleAotStatus.Active
                && initial.Child is { Name: "nested", Value: 31 },
            "The MessagePack facade did not read nested, nullable, enum, and collection members through the generated fragment formatter."
        );
    }

    File.Delete(settingsPath);
    await using var context = CreateMessagePackContext();
    var state = context.GetState<SampleAotSetting>();
    _ = await state.GetValueAsync();
    await state.SaveAsync(settings =>
    {
        settings.Endpoint.Name = "primary";
        settings.Endpoint.Value = 31;
        settings.Status = SampleAotStatus.Retired;
    });

    var fragmentRoundTrip = await state.GetValueAsync();
    Require(
        fragmentRoundTrip.Endpoint is { Name: "primary", Value: 31 }
            && fragmentRoundTrip.Status == SampleAotStatus.Retired,
        "The generated MessagePack fragment lost the custom POCO or enum member."
    );

    ConfiglueContext CreateMessagePackContext() =>
        ConfiglueApp.CreateContext(builder =>
            builder.Add<SampleAotSetting>(model =>
                model.Sources(sources =>
                    sources.FromGeneratedMessagePackFile(
                        new MessagePackFileSourceOptions
                        {
                            Path = settingsPath,
                            WatchChanges = false,
                        },
                        serializerOptions
                    )
                )
            )
        );
}

static void RequireFragmentCoverage(SampleAotSetting.Fragment fragment, string stage)
{
    Require(
        fragment.Endpoint.Value is { } endpoint
            && endpoint.Name.Value == "fragment"
            && endpoint.Value.Value == 23,
        $"The MessagePack {stage} round trip lost the custom POCO member."
    );
    Require(
        fragment.Endpoints.Value is { Count: 2 } pocoItems
            && pocoItems[0] is { Name: "first", Value: 47 }
            && pocoItems[1] is { Name: "second", Value: 59 },
        $"The MessagePack {stage} round trip lost the generic POCO collection."
    );
    Require(
        fragment.Numbers.Value is { Count: 3 } numbers
            && numbers[0] == 1
            && numbers[1] == 3
            && numbers[2] == 5,
        $"The MessagePack {stage} round trip lost the generic value collection."
    );
    Require(
        fragment.Label.IsPresent && fragment.Label.Value is null,
        $"The MessagePack {stage} round trip lost the present-but-null member."
    );
    Require(
        fragment.RetryLimit.Value == 3,
        $"The MessagePack {stage} round trip lost the nullable value member."
    );
    Require(
        fragment.Status.Value == SampleAotStatus.Active,
        $"The MessagePack {stage} round trip lost the enum member."
    );
    Require(
        fragment.Child.Value is { } child
            && child.Name.Value == "nested"
            && child.Value.Value == 31,
        $"The MessagePack {stage} round trip lost the nested fragment."
    );
}

/// <summary>
/// Explicit closed-world leaf formatters for the supported consumer's MessagePack
/// member types. The application's generated resolver covers custom POCO types; every
/// other scalar, nullable, enum, and generic collection member needs one explicit
/// formatter here so neither the built-in resolver's dynamic helper nor the standard
/// resolver fallback stays reachable.
/// </summary>
internal sealed class SupportedMessagePackLeafResolver : IFormatterResolver
{
    private static readonly NullableStringFormatter StringFormatter =
        NullableStringFormatter.Instance;
    private static readonly Int32Formatter IntFormatter = Int32Formatter.Instance;
    private static readonly NullableInt32Formatter NullableIntFormatter =
        NullableInt32Formatter.Instance;
    private static readonly IMessagePackFormatter<List<int>> NumberCollectionFormatter =
        new ListFormatter<int>()!;
    private static readonly IMessagePackFormatter<List<SampleAotPoco>> PocoCollectionFormatter =
        new ListFormatter<SampleAotPoco>()!;
    private static readonly IMessagePackFormatter<SampleAotStatus> StatusFormatter =
        MessagePackEnumFormatter<SampleAotStatus>.Instance;

    public IMessagePackFormatter<T>? GetFormatter<T>()
    {
        if (typeof(T) == typeof(string))
        {
            return (IMessagePackFormatter<T>)(object)StringFormatter;
        }

        if (typeof(T) == typeof(int))
        {
            return (IMessagePackFormatter<T>)(object)IntFormatter;
        }

        if (typeof(T) == typeof(int?))
        {
            return (IMessagePackFormatter<T>)(object)NullableIntFormatter;
        }

        if (typeof(T) == typeof(List<int>))
        {
            return (IMessagePackFormatter<T>)(object)NumberCollectionFormatter;
        }

        if (typeof(T) == typeof(List<SampleAotPoco>))
        {
            return (IMessagePackFormatter<T>)(object)PocoCollectionFormatter;
        }

        if (typeof(T) == typeof(SampleAotStatus))
        {
            return (IMessagePackFormatter<T>)(object)StatusFormatter;
        }

        return null;
    }
}
