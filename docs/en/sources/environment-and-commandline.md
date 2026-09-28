---
title: Environment and command line
description: Read-only sources from process variables and System.CommandLine.
---

Both sources are read-only and typically sit above file layers at higher priorities.

## Environment variables

`EnvironmentStateSource.FromEnvironment<TModel, TFragment>(id, prefix)` creates a read-only sparse source from process environment variables such as `APP__DATABASE__HOST`. Double underscores separate nested model members; member names are matched case-insensitively.

A property annotated with `[ConfiglueEnvironment("ENV_NAME")]` reads that mapped variable name case-insensitively instead, including for nested model properties. Common scalar values use invariant parsing, and a custom parser can handle application-specific types. The reader recalculates a content revision on each read; process environment variables do not provide a watcher.

```csharp
model.Sources(sources =>
{
    sources.Add(EnvironmentStateSource.FromEnvironment<AppConfig, AppConfig.Fragment>(
        "environment", "APP"));
});
```

Through the one-argument facade, the same source registers more concisely:

```csharp
sources.FromEnvironment(new()
{
    Prefix = "APP",
});
```

The facade derives an opaque source ID from the normalized prefix. Set `Id` only when a stable custom identity is needed for advanced source selection. The options also accept `EnvironmentVariables` (useful for tests or custom hosts) and a `ValueParser` override for application-specific scalar conversion.

## Command line

`Configlue.Source.CommandLine` accepts the application's existing `System.CommandLine` parse result and explicit symbol-to-path mappings. Parser defaults do not become overrides unless the symbol was explicitly supplied; a parse result with errors fails the source read. Map root and selected subcommand symbols explicitly, and create a new source/context when command-line input changes.

```csharp
using Configlue.Source.CommandLine;
using System.CommandLine;

model.Sources(sources => sources.FromCommandLine(new CommandLineSourceOptions
{
    Id = "command-line",
    ParseResult = parseResult,
    Priority = 500,
},
mappings =>
{
    mappings.Map<AppSettings, int>(portOption, settings => settings.Server!.Port);
    mappings.Map<AppSettings, bool>(verboseOption, settings => settings.Diagnostics!.Verbose);
}));
```

Mappings convert parsed values directly to member types without JSON serialization, so they work under NativeAOT. Attach a converter for custom shapes, fan one symbol out to several members, or target one member from several symbols (the last present mapping wins):

```csharp
mappings.Map(databaseOption, "Database.Host", static value => value?.Split(':')[0]);
mappings.Map(databaseOption, "Database.Port", static value => int.Parse(value?.Split(':')[1] ?? "0", CultureInfo.InvariantCulture));
mappings.Map(firstOption, "RetryCount");
mappings.Map(secondOption, "RetryCount"); // wins when both are present
```

The package builds against System.CommandLine 2.0.12 and uses only APIs that also exist in the 3.x line. To verify another line, build with `-p:ConfiglueSystemCommandLineVersion=<version>` (for example `3.0.0-rc.1.26425.128`).

## Next steps

* [HTTP and ZIP](./http-and-zip.md) for remote and archive sources.
* [Common layered sources](../basic-usage/common-sources.md) for the composed preset.
