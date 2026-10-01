# Configlue

**Read and write typed configuration, wherever it lives.**

Configlue is a .NET configuration state library. Application code works with typed state; Configlue handles where that state comes from, how multiple sources are combined, and where updates are persisted.

## Do you really need a configuration library?

If all you need is one JSON file, probably not.

```csharp
var json = File.ReadAllText("settings.json");
var settings = JsonSerializer.Deserialize<AppSettings>(json);

// ...

File.WriteAllText(
    "settings.json",
    JsonSerializer.Serialize(settings)
);
```

For a small application, that can be enough.

The problem is that configuration rarely stays small.

## Configuration grows into infrastructure

Consider applications you already use.

[Git](https://git-scm.com/docs/git-config) is a CLI tool, yet its configuration already has `system`, `global`, `local`, `worktree`, and command scopes. Reads follow precedence rules. Writes need an explicit target scope. Git can even tell you which scope and file a value came from.

[Visual Studio Code](https://code.visualstudio.com/docs/configure/settings) goes further. It has default, user, remote, workspace, workspace-folder, language-specific, profile, and policy settings, plus Settings Sync across machines. Some values are edited by humans in JSON, so editor assistance and schema-aware validation matter too.

Managed [Chrome](https://support.google.com/chrome/a/answer/9037717) adds another dimension: platform, machine-cloud, OS-user, and cloud-user policies, each with its own precedence and management semantics.

Your application may never become Git, VS Code, or Chrome. But configuration systems tend to grow in the same directions.

You start with one file. Then you add per-user settings, project-local overrides, environment variables, command-line switches, remote policy, credentials, profiles, or organization-wide defaults.

Then the difficult questions arrive.

Which source wins? Which source should receive a write? What if a higher-priority source is read-only? How do you distinguish a missing value from an explicit `null`? How do you preserve comments when humans edit the file? How do you expose a schema? What happens when the schema changes next year? How do you migrate old settings? How do you make writes atomic, retain backups, detect conflicts, and react to external changes?

You can implement all of this yourself.

Configlue exists so you do not have to.

## Reading configuration is solved. Writing is the missing half.

.NET already has an excellent read-side configuration ecosystem.

`IConfiguration` combines configuration providers into a unified view, and `IOptions<T>` gives application code strongly typed access through dependency injection. Microsoft explicitly describes `IConfiguration` as read-only and not designed for programmatic persistence: [Configuration in .NET](https://learn.microsoft.com/dotnet/core/extensions/configuration).

That is a good design for application configuration that is only consumed.

But many applications also *edit* configuration.

A settings screen changes the theme. A CLI command updates a profile. A desktop app saves the last selected device. A game changes graphics settings. An administration UI edits user-specific state.

At that point the application needs an abstraction for this:

```csharp
await settings.SaveAsync(...);
```

without also knowing whether the update belongs in a JSON file, browser storage, PostgreSQL, a user-scoped file, or another writable source.

Configlue makes that boundary explicit:

```csharp
IReadOnlyState<AppSettings>
IWritableState<AppSettings>
```

Your application says what to read or write. Infrastructure decides how.

## What Configlue gives you

- **Typed read/write state.** Application code consumes `IReadOnlyState<T>` or `IWritableState<T>`, without depending on storage, serialization, or routing details.
- **Layered configuration with provenance.** Combine user, local, environment, command-line, remote, database, or custom sources while retaining where each value came from and whether it can be edited.
- **Sparse, correct writes.** Generated patches preserve the difference between missing, default, `null`, set, and unset values instead of rewriting an entire model blindly.
- **Configuration that can evolve.** Version models, migrate old schemas, split or merge stored shapes, validate values, export JSON Schema, and move data between sources.
- **Production-grade persistence.** Change notifications, safe writes, backups, conflict handling, comments where supported, encryption, compression, and source-specific write policies are available without leaking into application code.
- **A broad .NET integration surface.** Use Configlue with plain .NET, dependency injection, Microsoft Options, ASP.NET Core, Blazor, desktop UI frameworks, MAUI, Unity, Godot, R3, NativeAOT, and custom infrastructure.

## The API your application sees

Most application code should look like this:

```csharp
var settings = ConfiglueApp.GetState<AppSettings>();

var current = await settings.GetValueAsync();

await settings.SaveAsync(patch =>
{
    patch.Theme = "Dark";
});
```

That is the important part.

The source layout, format, migration rules, write destination, validation, encryption, and hosting integration are configured outside the application logic.

## One API, every .NET app

Configlue keeps the state API consistent while adapting the hosting and persistence details to the application you are building.

### Plain .NET

For a normal CLI, utility, or desktop process, initialize Configlue once and use the process-wide state API.

```csharp
ConfiglueApp.Initialize(config =>
{
    // configure models and sources
});

var settings = ConfiglueApp.GetState<AppSettings>();
```

Use `ConfiglueApp.CreateContext(...)` when you explicitly need an isolated or lifetime-managed context. The process-wide `Initialize` / `GetState<T>` path is the normal entry point.

### Generic Host and ASP.NET Core

Register the same model definitions through dependency injection:

```csharp
builder.Services.AddConfiglue(config =>
{
    // configure models and sources
});
```

Then inject only what the application layer needs:

```csharp
public sealed class SettingsService(IWritableState<AppSettings> settings)
{
    // ...
}
```

ASP.NET Core hosting adds request/subject-aware integration where configuration needs to vary by the current user or tenant.

### Blazor

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

There is also a read-only `StateReader<T>`, session storage support, and integration with Blazor authentication state for subject-scoped configuration.

### Desktop UI: WPF, Windows Forms, WinUI, Avalonia

The shared `Configlue.Extensions.ComponentModel` package adapts state to `INotifyPropertyChanged`-style UI code:

```csharp
var reader = new ConfiglueStateReader<AppSettings>(
    state,
    dispatcher
);
```

The framework-specific hosting packages provide the native dispatcher/lifecycle bridge for WPF, Windows Forms, WinUI, and Avalonia without duplicating the underlying read/edit semantics.

### .NET MAUI

MAUI hosting maps Configlue's standard locations to the application sandbox:

```csharp
builder.UseMaui();
```

It also bridges UI dispatch and provides a small-secret resource backed by MAUI SecureStorage for credentials, tokens, API keys, or encryption key material.

### Unity

Unity hosting uses Unity's runtime semantics instead of pretending the player is a normal desktop process:

```csharp
builder.UseUnity();
```

User-global configuration maps to `Application.persistentDataPath`, while the portable Configlue packages remain usable for JSON, MessagePack, compression, AES, and R3 composition, including AOT-oriented scenarios.

### Godot

Godot hosting maps writable application state to Godot's user-data location:

```csharp
builder.UseGodot();
```

Configlue uses `user://` semantics for writable state and keeps the rest of the configuration pipeline host-independent.

## Install

For the common case, install the convenience package:

```bash
dotnet add package Configlue
```

Add platform, provider, resource, transformer, or integration packages only when you need them. Configlue is intentionally split so applications do not have to depend on every supported backend or host.

## Quick Start

The following single-file program uses a normal per-user configuration file without exposing file handling to the application code.

Save it as `example.cs` and run it with `dotnet run example.cs` (.NET 10 or later).

```csharp
#!/usr/bin/env dotnet
#:package Configlue@*

using Configlue;
using Configlue.Source.Presets;

[ConfiglueModel("sample.settings", Version = 1)]
public partial class AppSettings
{
    public string Name { get; set; } = "World";
    public string Theme { get; set; } = "System";
}

ConfiglueApp.Initialize(config =>
{
    config.UseCommonSources(sources =>
    {
        sources.WithUserGlobal("SampleApp");
        sources.Add<AppSettings>();
    });
});

var settings = ConfiglueApp.GetState<AppSettings>();

var current = await settings.GetValueAsync();
Console.WriteLine($"Hello, {current.Name}. Theme: {current.Theme}");

await settings.SaveAsync(patch =>
{
    patch.Name = "Alice";
    patch.Theme = "Dark";
});

await ConfiglueApp.ShutdownAsync();
```

The application only reads and writes `AppSettings`. The standard path, document format, sparse update, serialization, and safe persistence behavior stay in the configured infrastructure.

## Designed for both defaults and control

### Use it anywhere C# runs

C# is used for CLI tools, services, web applications, desktop UI, mobile applications, and games. Configlue keeps its portable core and integrations broadly targetable, including .NET Standard where practical, while host-specific packages contain platform-specific behavior.

The goal is not to force every runtime into the same assumptions. It is to keep the *application-facing state model* consistent while letting each host define the correct paths, lifecycle, storage, and threading semantics.

### Composable underneath

Configlue does not require one storage backend, one serialization format, or one source layout.

Files, HTTP, S3, Redis, PostgreSQL, browser storage, environment variables, command-line arguments, JSON, YAML, XML, MessagePack, AES, compression, and custom implementations can be composed according to the application.

If the default setup is wrong for your application, replace it.

### Convenient on top

Most applications still have predictable needs.

Configlue provides presets and host integrations for those cases so a normal application does not need to manually assemble every low-level component.

The design principle is:

**Composable at the bottom, convenient at the top.**

### Keep infrastructure out of application code

The application should usually depend on:

```csharp
IReadOnlyState<T>
IWritableState<T>
```

not on JSON, file paths, S3 clients, Redis connections, browser APIs, or migration implementations.

That keeps configuration infrastructure behind a stable application boundary and allows the underlying persistence strategy to change without rewriting the code that consumes settings.

## Ecosystem

Configlue is split into focused packages. The exact package set can grow without making the core abstraction larger.

| Area | Examples |
| --- | --- |
| Core | `Configlue`, `Configlue.Core`, `Configlue.Abstraction`, `Configlue.Testing` |
| Providers | JSON, YAML, XML, MessagePack |
| Sources | Environment, CommandLine, PostgreSQL |
| Resources | HTTP, Redis, S3 |
| Transformers | AES, Compression |
| Extensions | DI, Microsoft Options, ComponentModel, R3 |
| Hosting | ASP.NET Core, Blazor, WPF, Windows Forms, WinUI, Avalonia, MAUI, Unity, Godot |
| Tooling | Source generator, JSON Schema MSBuild generation |

The lower-level packages are there when you want control. Most applications can start with `Configlue` and add only the integrations they need.

## Why "Configlue"?

**Configuration + glue = Configlue.**

Configlue glues configuration infrastructure together so the rest of the application does not have to become configuration glue code.

There is also a small Japanese wordplay: **コンフィグる** (*config-ru*) reads naturally as "to config."

## License

Configlue is licensed under the Apache-2.0 License.
