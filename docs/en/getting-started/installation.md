---
title: Installation
description: Requirements and the packages needed to start using Configlue.
---

## Requirements

Configlue currently targets .NET 10. Install the .NET 10 SDK before creating an application.

For a new console application:

```sh
dotnet new console -n ConfiglueTutorial
cd ConfiglueTutorial
dotnet add package Configlue
```

The `Configlue` meta-package includes the core runtime, the JSON provider, common source presets, environment-variable support, dependency-injection integration, JSON Schema support, and the source generator.

## Optional packages

Add only the packages required by the application.

| Need | Package |
| --- | --- |
| YAML files | `Configlue.Provider.Yaml` |
| XML files | `Configlue.Provider.Xml` |
| YAML in common preset layers | `Configlue.Source.Presets.Yaml` |
| XML in common preset layers | `Configlue.Source.Presets.Xml` |
| `System.CommandLine` input | `Configlue.Source.CommandLine` |
| Microsoft `IOptions<T>` adapters | `Configlue.Extensions.MSOptions` |
| Rx.NET integration | `Configlue.Extensions.Reactive` |
| R3 integration | `Configlue.Extensions.R3` |
| ASP.NET Core integration (request subjects and HTTP resource endpoints) | `Configlue.Extensions.AspNetCore` |
| Blazor integration (authentication-state subjects and browser storage) | `Configlue.Extensions.Blazor` |
| Amazon S3 | `Configlue.Resource.S3` |
| Direct PostgreSQL storage | `Configlue.Source.PostgreSql` and `Configlue.Source.PostgreSql.Migrations` |
| Direct Redis storage | `Configlue.Resource.Redis` |
| ZIP archive entries | `Configlue.Resource.Zip` |
| AES-GCM encryption | `Configlue.Transformer.AES` |
| In-memory test doubles | `Configlue.Testing` |
| Provider development | `Configlue.Extensibility` |

See [Packages](../reference/packages.md) for the complete package map.

## Verify the source generator

Configlue models are `partial` classes marked with `[ConfiglueModel]`. Building the project generates the sparse Fragment, Patch, and details types used by the runtime.

```csharp
using Configlue;

[ConfiglueModel("example.health-check", Version = 1)]
public partial class HealthCheckSettings
{
    public bool Enabled { get; set; } = true;
}
```

The model ID is persisted as schema identity. Keep it stable after configuration files have been distributed.

Next: [Quick Start](./quick-start.md).
