using System.Text.Json;
using Configlue;
using Configlue.Provider.Json;
using Example.WorkerService;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var settingsPath = Path.Combine(AppContext.BaseDirectory, "usersettings.json");
var builder = Host.CreateApplicationBuilder(args);

// The host owns and disposes the file resource registered as a singleton.
builder.Services.AddSingleton(_ => new FileResource(settingsPath));
builder.Services.AddConfiglueOptions<SampleSetting, SampleSetting.Fragment>(
    provider =>
    {
        var resource = provider.GetRequiredService<FileResource>();
        var source = SerializedStateSource.FromResource<SampleSetting.Fragment>(
            "settings",
            resource,
            new JsonStateCodec<SampleSetting.Fragment>(
                new JsonSerializerOptions { WriteIndented = true }
            ),
            physicalOrigin: settingsPath
        );
        return new StateSourceSet<SampleSetting.Fragment>([source]);
    },
    StateWriteRoute.To("settings"),
    onChangeDebounce: TimeSpan.Zero
);
builder.Services.AddHostedService<Worker>();

await builder.Build().RunAsync();
