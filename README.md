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

> Full documentation lives at <https://arika0093.github.io/Configlue/>.
> Start there with [Getting Started](https://arika0093.github.io/Configlue/en/getting-started/) for the canonical tutorial; the sample below is the same first workflow in brief.

The first workflow is a single local JSON file: define a model, choose where it is
stored, read it, and save a patch. Save it as `example.cs` and run it with
`dotnet run example.cs` (.NET 10 or later).

```csharp
#!/usr/bin/env dotnet
#:package Configlue@*

using Configlue;
using Configlue.Provider.Json;

// 1. Initialize: one model declaration plus one file declaration.
ConfiglueApp.Initialize(config =>
{
    config.Add<AppSettings>().UseLocalJson("settings.json");
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

That is the whole getting-started surface: no source IDs, priorities, write routing,
Resource/Codec terms, or subject/profile vocabulary. The application consumes only
`IWritableState<AppSettings>` (`GetValueAsync` / `SaveAsync` / `OnChange`).

### Single-file defaults

`UseLocalJson` is tuned for ordinary application settings:

- A missing file reads as model defaults; the file (including its directory) is
  created on the first save.
- Writes replace the file atomically (temporary file plus rename) and keep one
  backup generation.
- The file is watched: external edits reload, and `OnChange` listeners observe them.
- A malformed document fails reads (the JSON format exception propagates) instead of
  silently returning defaults; the watcher reports the failure and recovers on the
  next valid write.
- Validation failures throw (`ConfiglueValidationException`); concurrent write
  conflicts fail instead of overwriting.
- Documents are plain simple-layout JSON (`{ "$version": 1, ... }`); comments are
  accepted on read.

### Grow without rewriting

Choosing the tiny-file path never forces a later migration to another library.
When the application grows, the same consumer code keeps working while the setup
gains layers — local file, then environment overrides (both included in the
default `Configlue` package):

```csharp
using Configlue.Source.Presets;

ConfiglueApp.Initialize(config =>
{
    config.UseCommonSources(sources =>
    {
        sources.WithLocal("settings.json");
        sources.WithEnvironment("APP_");
        sources.Add<AppSettings>();
    });
});

// Unchanged application code:
var settings = ConfiglueApp.GetState<AppSettings>();
var current = await settings.GetValueAsync();
await settings.SaveAsync(patch =>
{
    patch.Theme = "Dark";
});
```

Layering, provenance, migrations, and diagnostics stay available behind the same
`IWritableState<T>`; they just do not appear in the getting-started path.

Remote policy, command-line, YAML/MessagePack, and other integrations are
explicit opt-ins with their own packages:

```bash
dotnet add package Configlue.Source.Http
dotnet add package Configlue.Source.CommandLine
dotnet add package Configlue.Extensions.DI
```

```csharp
using Configlue.Source.Http;
using Configlue.Source.Presets;

config.UseCommonSources(sources =>
{
    sources.WithLocal("settings.json");
    sources.WithEnvironment("APP_");
    sources.WithHttpPolicy("https://example.com/policy", httpClient);
    sources.Add<AppSettings>();
});
```

### Runnable showcase

The Quick Start above stays lightweight and independent of Aspire. When you
want to click through the concepts — local writable settings, layered
configuration with provenance, write behavior, HTTP client/server state, and
PostgreSQL-backed state — run the single-Playground showcase:

```bash
dotnet run --project examples/Configlue.Examples.AppHost
```

See [examples/README.md](examples/README.md) for scenarios and standalone
alternatives.

## Installation
### Plain .NET (primary package)

Install the primary package for ordinary settings. It includes the runtime,
JSON, file/`FileResource`, ZIP-backed `SingleBinary`, environment sources,
standard paths, common presets, and the generator — with no DI, HTTP,
command-line, external-format, database, cloud, hosting, or specialized
dependencies:

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

### Generic Host (opt-in DI)

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

### Opt-in packages

`Configlue` is the primary package for ordinary settings. `Configlue.Core` is
the storage/format/host-neutral runtime and composition foundation for advanced
and package-author scenarios; normal applications rarely reference it directly.

Add only what the application actually uses:

- `Configlue.Extensions.DI` — Microsoft DI integration.
- `Configlue.Source.Http` — remote JSON-over-HTTP policy sources (including the
  `WithHttpPolicy` common preset).
- `Configlue.Source.CommandLine` — `System.CommandLine` parse-result sources.
- `Configlue.Provider.Yaml`, `Configlue.Provider.MessagePack`,
  `Configlue.Provider.Xml` — external-format providers.
- `Configlue.Transformer.AES`, `Configlue.Transformer.Compression` — encryption
  and compression (AES narrows TFMs and stays opt-in).
- Database, cloud, hosting, and other specialized backends.

## Why "Configlue"?

* It glues fragments of configuration into a single setting.
* It provides the glue code for configuration.
* In Japanese, *config-ru* sounds like a verb meaning "to configure."

## License

Configlue is licensed under the Apache-2.0 License.
