---
title: Common layered sources
description: Global, local, specific, and environment source presets.
---

# Common layered sources

The `Configlue.Source.Common` package, included by `Configlue`, composes the standard application stack — global file, local file, explicitly selected file, and environment variables — for either `CreateContext` or `services.AddConfiglue`. Command-line overrides are opt-in through `Configlue.Source.CommandLine`.

```csharp
using Configlue.Source.Common;

config.Add<AppSettings>(model =>
    model.UseCommonSources(
        "ExampleApp",
        specificFilePath: selectedPath, // selected file path, separate from member overrides
        environmentPrefix: "EXAMPLE",
        writeLayer: CommonSourceWriteLayer.BestAvailable));
```

This convention uses `settings.json` in the standard per-user directory and current directory. Pass `CommonSourceOptions` when you need a custom file name, serializer settings, or custom file resources.

`UseCommonSources` expands to these stable logical sources:

| Source ID | Priority | Included when | Location or input | Writable |
| --- | ---: | --- | --- | --- |
| `common.global` | 100 | `EnableGlobalFile` | `Path.Combine(GetStandardSaveDirectory(ApplicationId), GlobalFileName)` | Only when selected by `WriteLayer` |
| `common.local` | 200 | `EnableLocalFile` | `Path.GetFullPath(LocalFilePath ?? Path.Combine(Environment.CurrentDirectory, GlobalFileName))` | Only when selected by `WriteLayer` |
| `common.specific` | 300 | `EnableSpecificFile` and `SpecificFilePath` is set | `Path.GetFullPath(SpecificFilePath)` | Only when selected by `WriteLayer` |
| `common.environment` | 400 | `EnableEnvironment` and `EnvironmentPrefix` is set | Environment variables | No |

To add command-line overrides, install `Configlue.Source.CommandLine` and call its `UseCommonSources` overload with the existing parse result and explicit mappings. This registers `common.commandLine` at priority 500.

Higher priorities win for members present in more than one source. File sources fall through when the file is missing; other read failures propagate. Exactly one file is writable. `WriteLayer` defaults to `Global`, preserving an explicit destination. `BestAvailable` selects among enabled file layers at registration time: the highest `*WritePriority` wins first, then an existing writable file, then a writable existing directory, then registration order (global, local, specific). It probes existing directories with a temporary file that is deleted on close and creates only the selected destination directory. The selection is fixed for that options runtime; later permission changes can still make a write fail. Set the `Enable*` switches to omit layers, or set `LocalFilePath`, `SpecificFilePath`, `WriteLayer`, and the `*WritePriority` values to change locations and destination.

`ConfiglueStandardPaths.GetStandardSaveDirectory(applicationId)` returns the platform-standard per-user configuration directory plus the application identifier. The application chooses the file name.

## Next steps

* [Environment and command line](../sources/environment-and-commandline.md) for the underlying sources.
* [Write routing](../layering/write-routing.md) for per-path write ownership.
