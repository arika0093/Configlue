using Configlue;
using Configlue.Provider.Json;
using Example.ConsoleApp.NativeAot;

var settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
using var resource = new FileResource(settingsPath);

var modelSource = SerializedStateSource.FromResource<SampleSetting>(
    "settings",
    resource,
    new JsonStateCodec<SampleSetting>(SampleSettingJsonContext.Default.SampleSetting),
    physicalOrigin: settingsPath
);
var settingsSource = StateSourceProjection.Project(
    modelSource,
    static settings => SampleSetting.ToFragment(settings),
    static fragment => fragment.ToModel(),
    projectedSchema: SampleSetting.ConfiglueSchema.ToMetadata()
);

await using var context = global::Configlue.Configlue.CreateContext(builder =>
{
    builder.Add<SampleSetting>(settings =>
    {
        settings.Sources(sources => sources.Add(settingsSource));
        settings.WriteRoute = StateWriteRoute.To("settings");
    });
});
var options = context.GetOptions<SampleSetting>();
var firstRead = await options.ReadAsync();
var current = firstRead.Status switch
{
    StateReadStatus.Success => firstRead.Value!,
    StateReadStatus.NotFound => new SampleSetting(),
    _ => throw new IOException("The settings source is temporarily unavailable."),
};
Console.WriteLine($"Hello, {current.Name}. This is run {current.RunCount}.");

if (args.Length > 0)
{
    if (args.Length != 2 || args[0] != "--set-name")
    {
        Console.Error.WriteLine("Usage: Example.ConsoleApp.NativeAot [--set-name <name>]");
        return 2;
    }

    current.Name = args[1];
    current.RunCount++;
    var writer =
        settingsSource.Writer
        ?? throw new InvalidOperationException("The settings source is read-only.");
    await writer.WriteAsync(
        new StateWriteRequest<SampleSetting.Fragment>(SampleSetting.ToFragment(current))
    );

    var updatedRead = await options.ReadAsync();
    var updated = updatedRead.Status switch
    {
        StateReadStatus.Success => updatedRead.Value!,
        _ => throw new IOException("The saved settings could not be read."),
    };
    Console.WriteLine($"Saved: Hello, {updated.Name}. This is run {updated.RunCount}.");
}

Console.WriteLine($"Settings file: {settingsPath}");
return 0;
