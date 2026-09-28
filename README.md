# Configlue

**Make configuration management easier.**

Configlue combines typed settings from files, environment variables, command-line arguments, HTTP resources, and other sources. It keeps track of where values come from, so an update can change one setting without replacing unrelated settings.

## Why Configlue?
### Configurations are easy ... until they aren't.

Saving and loading a single JSON file is straightforward:

```cs
// load
var file = File.ReadAllText("settings.json");
var config = JsonSerializer.Deserialize<AppSettings>(file);
// save
var json = JsonSerializer.Serialize(config);
File.WriteAllText("settings.json", json);
```

…until it's not.

### Configurations checklist

As an application grows, you may need to:

* Store per-user defaults in the OS-standard configuration directory.
* Keep project-specific overrides in files in the current working directory.
* Add environment-variable overrides for containers or CI/CD when needed.
* Use command-line arguments for debugging or quick experiments.

`UseCommonSources` sets up the global and local file layers. Environment variables are opt-in through an environment prefix, and command-line values can be added with `Configlue.Source.CommandLine`. Additional sources can be registered with `model.Sources(...)`.

### Configlue's Approach

Configlue combines values contributed by independent sources into one typed model. This example illustrates the idea; each source must be configured by the application:

```jsonc
{
  "Name": "Alice",   // This value comes from global.
  "RunCount": 42,    // This value only exists in local.
  "Theme": "Dark",   // This value exists in both global and local, but local takes precedence.
  "Server": {
    "Host": "localhost:8080",  // This configuration was set via command-line arguments (-h localhost:8080).
    "Username": "alice",       // This value comes from an environment variable (MYAPP__SERVER__USERNAME).
    "Password": "secret"       // This value was decrypted from encrypted credentials.
  },
  "Features": {
    "EnableFeatureX": true,  // These settings come from a remote-managed HTTP policy.
    "EnableFeatureY": false  // This example's remote source is read-only.
  }
}
```

*Configlue* joins (**glues**) separate sources of **configuration** into one model.

### Check where values come from and save updates

Read the combined value with `GetValueAsync`. Use `GetDetailsAsync` to inspect its contributing sources:

```csharp
var options = context.GetOptions<AppSettings>();
// 1. Get the current value (merged from all sources)
var current = await options.GetValueAsync();
Console.WriteLine($"Hello, {current.Name}! (Run #{current.RunCount})");

// 2. Get the details of where each value came from
var details = await options.GetDetailsAsync();
// Values can be referenced normally.
Console.WriteLine($"Name came from {details.Name.Source?.Locator}");
Console.WriteLine($"Can write Name? {details.Name.IsEditable}");
foreach (var contribution in details.Name.Sources)
{
    var source = contribution.Source;
    Console.WriteLine(
        $"  {source.Kind} | {source.Locator} | writable: {source.CanWrite} | state: {contribution.State}"
    );
}
```

Save a patch to update only the members it specifies. `Unset` removes that source's contribution so a lower-priority source can provide the value:

```csharp
await options.SaveAsync(patch =>
{
    patch.Name = "Bob"; // Specify only the items you want to change
    patch.RunCount.Unset(); // Remove this source's value; a lower-priority source may provide one.
});
```

## Quick Start

Save the code below to `example.cs` and run it with `dotnet run example.cs` (requires .NET 10 or later).

```csharp
#!/usr/bin/env dotnet
#:package Configlue@*

using Configlue;
using Configlue.Source.Common;

// 1. Declare the settings model. The generator creates Fragment/Patch support.
[ConfiglueModel("SampleSetting", Version = 1)]
public partial class SampleSetting
{
    public string Name { get; set; } = "World";
    public int RunCount { get; set; }
    public bool DefaultValue { get; set; } = true;
}

// 2. Initialize using CommonSource (recommended default).
//    This automatically sets up standard user-level and local configuration layers.
await using var context = ConfiglueApp.CreateContext(conf =>
{
    conf.Add<SampleSetting>(model =>
    {
        // Global and local files; the prefix opts into the environment layer.
        model.UseCommonSources("SampleApp", environmentPrefix: "SAMPLE");
    });
});

// 3. Read and write through the options instance.
var options = context.GetOptions<SampleSetting>();
var current = await options.GetValueAsync();
Console.WriteLine($"Hello, {current.Name}! (Run #{current.RunCount})");

// Sparse edit: only modified fields are saved to the target layer.
await options.SaveAsync(patch =>
{
    patch.Name = "Alice";
    patch.RunCount = current.RunCount + 1;
    // DefaultValue is not modified, so it will not be saved to the target layer.
});

var updated = await options.GetValueAsync();
Console.WriteLine($"Saved. Hello, {updated.Name}! (Run #{updated.RunCount})");
```

`UseCommonSources` layers a file in the OS-standard per-user configuration directory and `settings.json` in the current working directory. Environment variables are not enabled unless you provide a prefix; here, `SAMPLE__NAME` overrides `Name`.

By default, `SaveAsync` writes to the local file. If you provide a specific file, that becomes the default write destination. Environment variables are read-only.

For command-line overrides, install `Configlue.Source.CommandLine` and map values from the `System.CommandLine` parse result. For complete examples and options, see the [CommonSource guide](https://arika0093.github.io/Configlue/en/basic-usage/common-sources/) and [tutorials](https://arika0093.github.io/Configlue/en/getting-started/quick-start/).

## FAQ
### Why not just use `IConfiguration`?

`Microsoft.Extensions.Configuration` is a good fit when an application needs to read configuration. Configlue is an alternative when settings also need to be combined from independent sources, inspected by provenance, or saved as sparse updates.

### I already have existing configuration files.

Existing files can be registered as sources. For schema changes, declare a migration from the previous version; see the [schema migration guide](https://arika0093.github.io/Configlue/en/migration/schema-migration/). The [adoption guide](https://arika0093.github.io/Configlue/en/migration/adopting-configuration-writable/) covers files from Configuration.Writable.

## License

This project is licensed under the Apache-2.0 License.
