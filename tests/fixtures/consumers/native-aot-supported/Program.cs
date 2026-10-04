using System.Buffers;
using System.Text.Json;
using Configlue;
using Configlue.Extensibility;
using Configlue.Provider.Json;
using Configlue.Resources;
using Configlue.Sources;
using Configlue.State;
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
