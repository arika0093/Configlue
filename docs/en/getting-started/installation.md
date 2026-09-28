---
title: Installation
description: SDK requirements and NuGet packages for Configlue.
---

# Installation

## Requirements

* .NET 10 SDK.
* `LangVersion` supporting source generators (the repository builds with `preview`; a recent C# is enough for the `new() { ... }` snippets in these guides).

Build and test the repository itself with:

```sh
dotnet build Configlue.slnx
dotnet test --solution Configlue.slnx --configuration Release
```

## Packages

Install the `Configlue` meta-package. It brings in the abstraction contracts, the core runtime, DI integration, the JSON provider, HTTP resources, common layered sources, the environment source, and the source-generator analyzer. It contains no implementation assembly of its own.

```bash
dotnet add package Configlue
```

Add the capability packages you need on top:

| Need | Package |
| --- | --- |
| YAML files | `Configlue.Provider.Yaml` |
| XML files | `Configlue.Provider.Xml` |
| `System.CommandLine` input | `Configlue.Source.CommandLine` |
| Global/local/specific/environment presets | `Configlue` (includes `Configlue.Source.Common`) |
| Serve settings over HTTP (ASP.NET Core) | `Configlue.Resource.Http.AspNetCore` |
| Persist state through a Dapr state store | `Configlue.Resource.Dapr` |
| Read and write Amazon S3 objects | `Configlue.Resource.S3` |
| ZIP archive entries | `Configlue.Resource.Zip` |
| In-memory doubles for tests | `Configlue.Testing` |

The full list with project roles is in the [package reference](../reference/packages.md).

## Verify the generator runs

Declare a model and build. If the build succeeds, the generator emitted the `Fragment`/`Patch` support types.

```csharp
using Configlue;

[ConfiglueModel("example.health-check", Version = 1)]
public partial class HealthCheckSettings
{
    public bool Enabled { get; set; } = true;
}
```

Models must be `partial`. The first constructor argument is the stable schema ID used for schema dispatch and JSON Schema export — pick a dotted name unique to your application.

Next: [Quick start](./quick-start.md).
