# Configlue

*Read and write typed configuration, wherever it lives.*

*Configlue* is a .NET configuration state library.
It takes care of the tedious parts of configuration management for you: retrieving data, merging it, monitoring state, and persisting it.

## Why Configlue?
### Do you really need a configuration library?

Probably not for simple cases. Something like this is enough:

```csharp
var json = File.ReadAllText("settings.json");
var settings = JsonSerializer.Deserialize<AppSettings>(json);
// ...
File.WriteAllText(
    "settings.json",
    JsonSerializer.Serialize(settings)
);
```

...until it isn't.

### Configuration grows into infrastructure

Consider the well-known applications you use every day.

#### [Git](https://git-scm.com/docs/git-config)
* Git is a CLI tool, yet its configuration already has `system`, `global`, `local`, `worktree`, and command scopes.
* Reads follow precedence rules. Writes need an explicit target scope.
* Git can even tell you which scope and file a value came from.

#### [Visual Studio Code](https://code.visualstudio.com/docs/configure/settings)
* VS Code goes further: default, user, remote, workspace, workspace-folder, language-specific, profile, and policy settings, plus Settings Sync across machines.
* Some values are edited by humans in JSON, so editor assistance and schema-aware validation matter too.

#### [Chrome](https://support.google.com/chrome/a/answer/9037717)
* Chrome adds another dimension: platform, machine-cloud, OS-user, and cloud-user policies,
* each with its own precedence and management semantics.

Now think about implementing all of that yourself. Can you come up with a good way to do it?

### The write side lacks an abstraction

For *reading* configuration, a good abstraction already exists: `IOptionsMonitor<T>`.
It looks simple, but it also covers change detection, so it has most of what you need.

But what about *writing*? In my experience, I have wanted an abstraction like this many times:

```csharp
await settings.SaveAsync(...);
```

The important point is that the application should not care *where* or *how* a setting is written.
All it needs is an API where you simply declare "read this" and "write this".

---

Configlue was designed as a solution to address these problems.

## Overview
### Simple to use

Before the feature list, we want you to know whether it is actually simple to use.
For most use cases, an application should need nothing more than this API:

```csharp
var settings = ConfiglueApp.GetState<AppSettings>();
// load
var current = await settings.GetValueAsync();
// monitor changes
settings.OnChange(changed => {
    // ...
});
// save (changed only)
await settings.SaveAsync(patch =>
{
    patch.Theme = "Dark";
});
```

Configlue makes this possible.

Of course, more detailed APIs (such as where a value came from, or whether it can be edited) are available when you need them.

And the detailed configuration itself only has to happen once, at application startup (or whenever you need it).

### What Configlue gives you

- **Typed read/write state.** Application code consumes `IReadOnlyState<T>` or `IWritableState<T>`, without depending on storage, serialization, or routing details.
- **Layered configuration with provenance.** Combine user, local, environment, command-line, remote, database, or custom sources while retaining where each value came from and whether it can be edited.
- **Configuration that can evolve.** Version models, migrate old schemas, split or merge stored shapes, validate values, export JSON Schema, and move data between sources.
- **Production-grade persistence.** Change notifications, safe writes, backups, conflict handling, comments where supported, encryption, compression, and source-specific write policies are available without leaking into application code.
- **A broad .NET integration surface.** Use Configlue with plain .NET, dependency injection, Microsoft Options, ASP.NET Core, Blazor, desktop UI frameworks, MAUI, Unity, Godot, R3, NativeAOT, and custom infrastructure.

## Quick Start

Save it as `example.cs` and run it with `dotnet run example.cs` (.NET 10 or later).

```csharp
#!/usr/bin/env dotnet
#:package Configlue@*

using Configlue;
using Configlue.Source.Presets;

// 1. Initialize
ConfiglueApp.Initialize(config =>
{
    config.UseCommonSources(sources =>
    {
        sources.WithLocal();
        sources.Add<AppSettings>();
    });
});

// 2. Read
var settings = ConfiglueApp.GetState<AppSettings>();
var current = await settings.GetValueAsync();
Console.WriteLine($"Hello, {current.Name}. Theme: {current.Theme}");

// 3. Save
await settings.SaveAsync(patch =>
{
    patch.Name = "Alice";
    patch.Theme = "Dark";
});

await ConfiglueApp.ShutdownAsync();

// 4. Define the settings class
[ConfiglueModel("sample.settings", Version = 1)]
public partial class AppSettings
{
    public string Name { get; set; } = "World";
    public string Theme { get; set; } = "System";
}
```

## Installation
### Plain .NET

Install the convenience package:

```bash
dotnet add package Configlue
```

For a normal CLI, utility, or desktop process, initialize Configlue once and use the process-wide state API.

```csharp
ConfiglueApp.Initialize(config =>
{
    // configure models and sources
});

var settings = ConfiglueApp.GetState<AppSettings>();
```

### Generic Host

Install the following packages:

```bash
dotnet add package Configlue
dotnet add package Configlue.Extensions.DI
```

Register the same model definitions through dependency injection:

```csharp
builder.Services.AddConfiglue(config =>
{
    // configure models and sources
});
```

Then inject only what the application layer needs:

```csharp
public class SettingsService(IWritableState<AppSettings> settings)
{
    // ...
}
```

### ASP.NET

Install the following packages:

```bash
dotnet add package Configlue
dotnet add package Configlue.Hosting.AspNetCore
```

In addition to the Generic Host features above, you can expose the typed effective state over `HTTP`.

```csharp
app.MapConfiglueState<AppSettings>("/api/settings");
```

Clients consume it as a normal Configlue source:

```csharp
config.Add<AppSettings>(model =>
    model.UseHttpState(
        "https://example.com/api/settings",
        options => options.Writable = true));
```

### Blazor

Install the following packages:

```bash
dotnet add package Configlue
dotnet add package Configlue.Hosting.Blazor
```

Blazor can use browser storage as a normal Configlue source:

```csharp
builder.Services.AddConfiglue(config =>
{
    config.Add<AppSettings>(model =>
        model.UseLocalStorage("app-settings"));
});
```

For UI editing, `StateEditor<T>` owns an edit session and exposes a standard Blazor `EditContext`:

```razor
<StateEditor T="AppSettings" Context="settings">
    <EditForm EditContext="settings.EditContext">
        ...
    </EditForm>
</StateEditor>
```

### Windows Applications

Install the following packages:

```bash
dotnet add package Configlue
dotnet add package Configlue.Hosting.Avalonia
# Or the following are available:
#   Configlue.Hosting.WinForm
#   Configlue.Hosting.WPF
#   Configlue.Hosting.WinUI
```

> TODO

### MAUI

Install the following packages:

```bash
dotnet add package Configlue
dotnet add package Configlue.Hosting.Maui
```

> TODO

### Unity

Download the following packages with [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity):

* Configlue
* Configlue.Hosting.Unity

> TODO

### Godot

Install the following packages:

```bash
dotnet add package Configlue
dotnet add package Configlue.Hosting.Godot
```

> TODO

## Why "Configlue"?

* It glues fragments of configuration into a single setting.
* It provides the glue code for configuration.
* In Japanese, *config-ru* sounds like a verb meaning "to configure."

## License

Configlue is licensed under the Apache-2.0 License.
