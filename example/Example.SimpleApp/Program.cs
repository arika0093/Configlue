using System.Text.Json;
using Configlue;
using Configlue.Provider.Json;
using Example.SimpleApp;

var settingsPath = Path.Combine(AppContext.BaseDirectory, "usersettings.json");

// FileResource provides the atomic JSON file and detects edits made outside this process.
using var resource = new FileResource(settingsPath);

// Read and write the generated fragment as JSON, linked to its backing file.
var source = SerializedStateSource.FromResource<SampleSetting.Fragment>(
    "settings",
    resource,
    new JsonStateCodec<SampleSetting.Fragment>(new JsonSerializerOptions { WriteIndented = true }),
    physicalOrigin: settingsPath
);

// Combine the source and its write route directly, without a dependency injection container.
await using var options = new ConfiglueOptions<SampleSetting, SampleSetting.Fragment>(
    new StateSourceSet<SampleSetting.Fragment>([source]),
    StateWriteRoute.To("settings")
);
var writable = (IWritableOptions<SampleSetting>)options;

var current = await writable.GetValueAsync();
Console.WriteLine($"Hello, {current.Name}. This is run {current.RunCount}.");

if (args.Length > 0)
{
    if (args.Length != 2 || args[0] != "--set-name")
    {
        Console.Error.WriteLine("Usage: Example.SimpleApp [--set-name <name>]");
        return 2;
    }

    await writable.SavePatchAsync(patch =>
    {
        patch.Name = args[1];
        patch.RunCount = current.RunCount + 1;
    });

    var updated = await writable.GetValueAsync();
    Console.WriteLine($"Saved: Hello, {updated.Name}. This is run {updated.RunCount}.");
}

Console.WriteLine($"Settings file: {settingsPath}");
return 0;
