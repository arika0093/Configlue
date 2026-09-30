# Configlue

*Make easy configuration management.*

Configlue is a .NET library that handles tedious configuration management for you.

## Why Configlue?
### Configurations are easy ... until it's not.

You might think that handling configuration files is straightforward and there's no need to use a library.
In fact, if you just need to save and load configuration from a JSON file, it's quite simple.

```cs
// load
var file = File.ReadAllText("settings.json");
var config = JsonSerializer.Deserialize<AppSettings>(file);
// save
var json = JsonSerializer.Serialize(config);
File.WriteAllText("settings.json", json);
```

... until it's not.

### Configurations checklist

Consider the following (detailed, yet important) use cases that you'll probably want to avoid dealing with manually:

<details>
<summary>Configuration comes from multiple locations</summary>

* Global configuration (`%XDG_CONFIG_HOME%/MyApp/settings.json`)
* Per-runtime folder configuration (`./myapp.json`, etc.)
* Environment variable overrides
* Command-line argument overrides
* Encrypted credentials (only part of the configuration)
* Sometimes not local at all. For example, corporate policies or HTTP APIs for centralized management.
</details>

<details>
<summary>You want to receive notifications when settings are updated</summary>

* When a configuration file is rewritten, you want it reflected without restarting the application.
</details>

<details>
<summary>Think about when you write configuration files</summary>

* When reading from multiple sources, you want to automatically choose the right place to write.
* When reading values from environment variables, you'd want to raise a write error.
</details>

<details>
<summary>Configuration files are sometimes written by humans</summary>

* They contain comments. Don't remove them.
* You want JSON schema support (since humans write them, you obviously want it!)
* What if there's a broken configuration file?
</details>

<details>
<summary>If the value is still at its default, don't write it to the configuration file</summary>

* We don't want to write `foo: null, bar: null`.
* But if the user writes `foo: null`, we need to respect that.
</details>

<details>
<summary>You want to version up configuration files</summary>

* Single values might become arrays, multiple items might be grouped or separated.
* In such cases, you want to automatically convert old configurations to the new format.
</details>

<details>
<summary>You want backups too</summary>

* When rewriting configuration files, you want to automatically backup old settings.
* It would be nice if backups were automatically cleaned up, removing old ones.
</details>

<details>
<summary>File writes are done safely</summary>

* Ensure atomicity so the file doesn't get corrupted if the app crashes during writing.
* If another process rewrites the same file during writing, detect the conflict and raise an error (or auto-merge).
* Automatically retry on failure.
</details>

Implementing all of these yourself is, frankly, tedious.

### Configlue's Approach

Configlue simplifies complex configuration management by keeping track of the source of each configuration and combining them at the end.
For example, like this:

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
    "EnableFeatureY": false  // (of course, they can't be written)
  }
}
```

*Configlue* joins (**glues**) separate sources of **configuration** into one model.

### Check where values come from and save updates

Read the combined value with `GetValueAsync`. Use `GetDetailsAsync` to inspect its contributing sources:

```csharp
var state = context.GetState<AppSettings>();
// 1. Get the current value (merged from all sources)
var current = await state.GetValueAsync();
Console.WriteLine($"Hello, {current.Name}! (Run #{current.RunCount})");

// 2. Get the details of where each value came from
var details = await state.GetDetailsAsync();
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
await state.SaveAsync(patch =>
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
using Configlue.Source.Presets;

// 1. Declare the settings model. The generator creates Fragment/Patch support.
[ConfiglueModel("SampleSetting", Version = 1)]
public partial class SampleSetting
{
    public string Name { get; set; } = "World";
    public int RunCount { get; set; }
    public bool DefaultValue { get; set; } = true;
}

// 2. Declare the preset layers. Only the sources listed here are enabled.
await using var context = ConfiglueApp.CreateContext(conf =>
    conf.UseCommonSources(sources =>
    {
        sources.WithUserGlobal("SampleApp");
        sources.WithLocal();
        sources.WithEnvironment("SAMPLE");
        sources.Add<SampleSetting>();
    })
);

// 3. Read and write through the state instance.
var state = context.GetState<SampleSetting>();
var current = await state.GetValueAsync();
Console.WriteLine($"Hello, {current.Name}! (Run #{current.RunCount})");

// Sparse edit: only modified fields are saved to the target layer.
await state.SaveAsync(patch =>
{
    patch.Name = "Alice";
    patch.RunCount = current.RunCount + 1;
    // DefaultValue is not modified, so it will not be saved to the target layer.
});

var updated = await state.GetValueAsync();
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
