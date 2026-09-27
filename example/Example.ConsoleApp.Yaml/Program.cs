using System.Text.Json;
using Configlue;
using Configlue.Provider.Yaml;
using Example.ConsoleApp.Yaml;
using Microsoft.Extensions.DependencyInjection;

var settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.yaml");
using var resource = new FileResource(settingsPath);

var source = SerializedStateSource.FromResource<SampleSetting.Fragment>(
    "settings",
    resource,
    new YamlStateCodec<SampleSetting.Fragment>(
        JsonNamingPolicy.CamelCase,
        SampleSetting.FragmentSchema
    ),
    physicalOrigin: settingsPath
);
var services = new ServiceCollection();
services.AddConfiglueOptions<SampleSetting, SampleSetting.Fragment>(
    new StateSourceSet<SampleSetting.Fragment>([source]),
    StateWriteRoute.To("settings"),
    onChangeDebounce: TimeSpan.Zero
);

using var serviceProvider = services.BuildServiceProvider();
var settings = serviceProvider.GetRequiredService<IWritableOptions<SampleSetting>>();
var current = await settings.GetValueAsync();
Console.WriteLine($"Hello, {current.Name}. This is run {current.RunCount}.");

if (args.Length > 0)
{
    if (args.Length != 2 || args[0] != "--set-name")
    {
        Console.Error.WriteLine("Usage: Example.ConsoleApp.Yaml [--set-name <name>]");
        return 2;
    }

    using (var edit = await settings.OpenEditSessionAsync())
    {
        edit.Value.Name = args[1];
        edit.Value.RunCount++;
        await edit.CommitAsync();
    }

    var updated = await settings.GetValueAsync();
    Console.WriteLine($"Saved: Hello, {updated.Name}. This is run {updated.RunCount}.");
}

Console.WriteLine($"Settings file: {settingsPath}");
return 0;
