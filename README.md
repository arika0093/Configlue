# Configlue

Typed configuration assembled from independent state sources.

Configlue is a source-generator-first .NET library for reading, resolving, editing, and persisting typed configuration. Each source contributes only the fields it owns, so values can be layered — JSON files, environment variables, command-line options, HTTP resources — without replacing an entire settings object.

Browse the [Configlue documentation site](https://arika0093.github.io/Configlue/) for comprehensive guides, tutorials, and API concepts.

## Key Features

* **Sparse Generated Fragments**: Read, resolve, and update only the fields you touch, leaving other settings and files intact.
* **Standard-by-Default Architecture**: Use `CommonSource` out of the box to manage global (per-user) settings, local overrides, and environment variables with zero boilerplate.
* **Extensible Layering**: Start with `CommonSource` and seamlessly extend with command-line arguments, remote HTTP policies, or custom sources via `model.Sources(...)`.
* **Safe Persistence**: Built-in atomic file writes with automatic backups, conflict detection, and debounce-enabled change notifications.
* **Universal .NET Support**: Works with or without DI (Console, Desktop, ASP.NET Core, Worker Services) and supports NativeAOT.

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

For more details, see the [tutorials](https://arika0093.github.io/Configlue/en/getting-started/quick-start/).

## FAQ
### Why not just use `IConfiguration`?

`Microsoft.Extensions.Configuration` is a good fit when an application needs to read configuration. Configlue is an alternative when settings also need to be combined from independent sources, inspected by provenance, or saved as sparse updates.

### I already have existing configuration files.

Existing files can be registered as sources. For schema changes, declare a migration from the previous version; see the [schema migration guide](https://arika0093.github.io/Configlue/en/migration/schema-migration/). The [adoption guide](https://arika0093.github.io/Configlue/en/migration/adopting-configuration-writable/) covers files from Configuration.Writable.

## License

This project is licensed under the Apache-2.0 License.
