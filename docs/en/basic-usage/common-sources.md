---
title: Common layered sources
description: Global, local, specific, and environment source presets.
---

# Common layered sources

The `Configlue.Source.Common` package, included by `Configlue`, composes the standard application stack — global file, local file, optional specific file, and optional environment variables — for either `CreateContext` or `services.AddConfiglue`. Command-line overrides are opt-in through `Configlue.Source.CommandLine`.

```csharp
using Configlue.Source.Common;

config.Add<AppSettings>(model =>
    model.UseCommonSources(
        "ExampleApp",
        specificFilePath: selectedPath,
        environmentPrefix: "EXAMPLE"));
```

This convention uses `settings.json` in the standard per-user directory and current directory. A specific file is included when `specificFilePath` is supplied. The environment layer is included when `environmentPrefix` is supplied. Pass `CommonSourceOptions` when you need a custom file name, serializer settings, or custom file resources.

`UseCommonSources` registers these layers in precedence order:

| Layer | Priority | Included when | Location or input | Writes |
| --- | ---: | --- | --- | --- |
| Global | 100 | Always | Standard per-user directory and `GlobalFileName` | Explicitly selectable |
| Local | 200 | Always | `LocalFilePath`, or current directory and `GlobalFileName` | Default when no specific path is supplied |
| Specific | 300 | `SpecificFilePath` is supplied | Selected path | Default when supplied |
| Environment | 400 | `EnvironmentPrefix` is supplied | Environment variables | Read-only |

To add command-line overrides, install `Configlue.Source.CommandLine` and call its `UseCommonSources` overload with the existing parse result and explicit mappings. This registers `common.commandLine` at priority 500.

The preset uses stable logical IDs internally. For source-local writes, use the semantic selector instead of creating a string-based key:

```csharp
await options.Source(CommonSource.Local).SaveAsync(
    new AppSettings.Patch { Name = FragmentOperation<string>.Set("local-name") }
);
```

All configured file layers are writable through explicit source handles. Ordinary `SaveAsync` writes to the specific file when one is supplied and otherwise to the local file. Set `WriteLayer` only when the ordinary write destination should differ from that convention. Layer inclusion depends on the specific path and environment prefix; the preset does not probe the file system during registration.

`ConfiglueStandardPaths.GetStandardSaveDirectory(applicationId)` returns the platform-standard per-user configuration directory plus the application identifier. The application chooses the file name.

## Next steps

* [Environment and command line](../sources/environment-and-commandline.md) for the underlying sources.
* [Write routing](../layering/write-routing.md) for per-path write ownership.
