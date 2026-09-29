using System.Buffers;
using System.Text.Json;
using Configlue;
using Configlue.Provider.Json;
using Configlue.Provider.Yaml;
using Example.ConsoleApp.NativeAot;

VerifyYamlNativeAotCodec();

var settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
var databasePath = Path.Combine(AppContext.BaseDirectory, "database.json");
var serializerOptions = new JsonSerializerOptions
{
    TypeInfoResolver = SampleSettingJsonContext.Default,
};

await using var context = ConfiglueApp.CreateContext(builder =>
{
    builder.Add<SampleSetting>(settings =>
    {
        settings.Sources(sources =>
        {
            sources.JsonFile(settingsPath).SerializerOptions(serializerOptions).WatchChanges(false);
            sources
                .JsonFile(databasePath)
                .Mount(value => value.Database)
                .Priority(100)
                .SerializerOptions(serializerOptions)
                .WatchChanges(false);
        });
    });
});
var options = context.GetState<SampleSetting>();
var current = await options.GetValueAsync();
Console.WriteLine(
    $"Hello, {current.Name}. This is run {current.RunCount}. Database: {current.Database.Host}:{current.Database.Port}."
);

if (args.Length > 0)
{
    if (args.Length != 2)
    {
        Console.Error.WriteLine(
            "Usage: Example.ConsoleApp.NativeAot [--set-name <name> | --set-database-host <host>]"
        );
        return 2;
    }

    if (args[0] == "--set-name")
    {
        await options.SaveAsync(settings =>
        {
            settings.Name = args[1];
            settings.RunCount = current.RunCount + 1;
        });
    }
    else if (args[0] == "--set-database-host")
    {
        await options.SaveAsync(settings => settings.Database!.Host = args[1]);
    }
    else
    {
        Console.Error.WriteLine(
            "Usage: Example.ConsoleApp.NativeAot [--set-name <name> | --set-database-host <host>]"
        );
        return 2;
    }

    var updated = await options.GetValueAsync();
    Console.WriteLine($"Saved: Hello, {updated.Name}. This is run {updated.RunCount}.");
}

Console.WriteLine($"Settings file: {settingsPath}");
Console.WriteLine($"Database file: {databasePath}");
return 0;

static void VerifyYamlNativeAotCodec()
{
    var codec = new YamlStateCodec<SampleSetting.Fragment>(
        modelSchema: SampleSetting.FragmentSchema,
        serializerOptions: SampleSettingYamlContext.Default.Options
    );
    var fragment = SampleSetting.Fragment.From(
        new SampleSetting { Name = "NativeAOT YAML", RunCount = 7 }
    );
    var buffer = new ArrayBufferWriter<byte>();
    var context = new StateCodecContext(SampleSetting.ConfiglueSchema.ToMetadata());
    codec.Serialize(fragment, buffer, in context);
    var sequence = new ReadOnlySequence<byte>(buffer.WrittenMemory);
    var decoded = codec.Deserialize(in sequence, default);
    if (decoded?.Name.Value != "NativeAOT YAML" || decoded.RunCount.Value != 7)
    {
        throw new InvalidOperationException("The SharpYaml NativeAOT round trip failed.");
    }
}
