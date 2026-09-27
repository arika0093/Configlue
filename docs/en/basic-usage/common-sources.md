---
title: Common layered sources
description: Global, local, specific, environment, and command-line presets.
---

# Common layered sources

The optional `Configlue.Source.Common` package composes the standard application stack — global file, local file, explicitly selected file, environment variables, command line — for either `CreateContext` or `services.AddConfiglue`:

```csharp
using Configlue.Source.Common;

config.Add<AppSettings>(model => model.UseCommonSources(new CommonSourceOptions
{
    ApplicationId = "ExampleApp",
    GlobalFileName = "settings.json",
    SpecificFilePath = selectedPath, // selected file path, separate from member overrides
    EnvironmentPrefix = "EXAMPLE",
    CommandLineParseResult = parseResult,
    ConfigureCommandLineMappings = mappings => mappings.Map(portOption, "Server.Port"),
    WriteLayer = CommonSourceWriteLayer.Global,
}));
```

`UseCommonSources` expands to these stable logical sources:

| Source ID | Priority | Included when | Location or input | Writable |
| --- | ---: | --- | --- | --- |
| `common.global` | 100 | `EnableGlobalFile` | `Path.Combine(GetStandardSaveDirectory(ApplicationId), GlobalFileName)` | Only when selected by `WriteLayer` |
| `common.local` | 200 | `EnableLocalFile` | `Path.GetFullPath(LocalFilePath ?? Path.Combine(Environment.CurrentDirectory, GlobalFileName))` | Only when selected by `WriteLayer` |
| `common.specific` | 300 | `EnableSpecificFile` and `SpecificFilePath` is set | `Path.GetFullPath(SpecificFilePath)` | Only when selected by `WriteLayer` |
| `common.environment` | 400 | `EnableEnvironment` and `EnvironmentPrefix` is set | Environment variables | No |
| `common.commandLine` | 500 | `EnableCommandLine` and `CommandLineParseResult` is set | Mappings from the existing parse result | No |

Higher priorities win for members present in more than one source. File sources fall through when the file is missing; other read failures propagate. Exactly the file selected by `WriteLayer` is writable, and selecting a disabled or unavailable file layer throws during registration. The command-line selection of `SpecificFilePath` is separate from member-level mappings; a parse result requires `ConfigureCommandLineMappings`. Set the `Enable*` switches to omit layers, or set `LocalFilePath`, `SpecificFilePath`, and `WriteLayer` to change their locations and destination.

`ConfiglueStandardPaths.GetStandardSaveDirectory(applicationId)` returns the platform-standard per-user configuration directory plus the application identifier. The application chooses the file name.

## Next steps

* [Environment and command line](../sources/environment-and-commandline.md) for the underlying sources.
* [Write routing](../layering/write-routing.md) for per-path write ownership.
