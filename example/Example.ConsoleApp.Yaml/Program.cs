using Configlue;
using Configlue.Provider.Yaml;
using Example.ConsoleApp.Yaml;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.Serialization.NamingConventions;

var settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.yaml");
using var resource = new FileResource(settingsPath);

var source = SerializedStateSource.FromResource<SampleSetting.Fragment>(
    "settings",
    resource,
    new YamlStateCodec<SampleSetting.Fragment>(CamelCaseNamingConvention.Instance),
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

    await settings.SaveAsync(value =>
    {
        value.Name = args[1];
        value.RunCount++;
    });

    var updated = await settings.GetValueAsync();
    Console.WriteLine($"Saved: Hello, {updated.Name}. This is run {updated.RunCount}.");
}

Console.WriteLine($"Settings file: {settingsPath}");
return 0;
