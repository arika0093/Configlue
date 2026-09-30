---
title: Packages
description: Find the NuGet package for each Configlue capability.
---

| Package | Purpose |
| --- | --- |
| `Configlue` | Portable convenience package: common layered and single-binary presets plus Core, the Microsoft dependency-injection integration, JSON provider, HTTP resources (including named `IHttpClientFactory` support), environment source, and the generator analyzer. AES is opt-in. |
| `Configlue.Abstraction` | Provider, codec, resource, and generated-model contracts. |
| `Configlue.Core` | Framework-neutral serializer-neutral state resolution runtime, builder/context model, provider-authoring helpers, ZIP resources, and common file-preset SPI. Usable without a dependency-injection container. |
| `Configlue.Extensions.DI` | Microsoft dependency-injection integration: `IServiceCollection` registration for generated state, profiles, per-subject host integration, and scoped/keyed lifetimes. |
| `Configlue.Extensions.MSOptions` | Optional adapters for Microsoft's options interfaces, including the `IValidateOptions<T>` validator bridge. |
| `Configlue.Extensions.R3` | Optional R3 observables for composing values, profiles, and reload signals. |
| `Configlue.Generator` | Generated sparse model support (Roslyn analyzer). |
| `Configlue.Testing` | In-memory resources and test doubles. |
| `Configlue.Provider.Json` | JSON codec, section resources, file registrations, and common-preset JSON selection. |
| `Configlue.JsonSchema.MSBuild` | Build-time JSON Schema generation for Configlue models. Direct-only development dependency; not included by the `Configlue` meta-package. |
| `Configlue.Provider.Xml` | XML codec with section resources, file registrations, and common-preset XML selection. |
| `Configlue.Provider.Yaml` | YAML codec with section resources, file registrations, and common-preset YAML selection. |
| `Configlue.Source.Environment` | Read-only source backed by process environment variables. |
| `Configlue.Source.CommandLine` | Read-only source backed by a `System.CommandLine` parse result, with opt-in common preset integration. |
| `Configlue.Resource.Http` | HTTP read/write resources with ETag revisions, polling change detection, JSON-over-HTTP source registration, and named `IHttpClientFactory` source support. |
| `Configlue.Extensions.AspNetCore` | ASP.NET Core host integration: current-request subject resolution (`IHttpContextAccessor`) and HTTP resource endpoints for serving resources. |
| `Configlue.Extensions.Blazor` | Blazor host integration: authentication-state subject resolution with circuit change notifications and browser `localStorage`/`sessionStorage` resources. |
| `Configlue.Resource.S3` | Optional Amazon S3 object resources and source registration with ETag revisions. |
| `Configlue.Source.PostgreSql` | Optional JSONB-native PostgreSQL source with subject-key rows, atomic revision checks, and `LISTEN`/`NOTIFY` change watching. |
| `Configlue.Source.PostgreSql.Migrations` | Database schema creation and per-component version migrations for `Configlue.Source.PostgreSql`. |
| `Configlue.Resource.Redis` | Optional Redis byte resources with subject-key keys, atomic Lua revision checks, and multiplexed Pub/Sub invalidation. |
| `Configlue.Transformer.AES` | AES-GCM encryption and authentication for state bytes between a Resource and Codec, including passphrase-based key derivation. |

## Target frameworks and direct dependencies

| Package | Assets | Direct NuGet dependencies and minimum-TFM notes |
| --- | --- | --- |
| `Configlue.Abstraction` | `netstandard2.0;netstandard2.1;net10.0` | `System.Memory` 4.6.3 and `Microsoft.Bcl.AsyncInterfaces` 10.0.5 on `netstandard2.0` only; the `netstandard2.1` asset uses the platform async-iterator surface without compatibility packages. |
| `Configlue.Core` | `netstandard2.0;netstandard2.1;net10.0` | `Microsoft.Extensions.Logging.Abstractions` 10.0.0, `System.IO.Hashing` 10.0.0, and `System.IO.Pipelines` 10.0.0; `System.ComponentModel.Annotations` 5.0.0 and `System.Threading.Channels` 10.0.5 on both Standard assets, with `Microsoft.Bcl.AsyncInterfaces` 10.0.5 and `System.Threading.Tasks.Extensions` 4.6.3 on `netstandard2.0` only. Core has no `System.Text.Json` and no Microsoft dependency-injection or options package dependency. |
| `Configlue` | `netstandard2.0;netstandard2.1;net10.0` | No direct NuGet dependencies; project references provide the portable Core/DI/JSON/HTTP/environment graph. AES is not referenced. |
| `Configlue.Extensions.DI` | `netstandard2.0;netstandard2.1;net10.0` | `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.0. |
| `Configlue.Extensions.MSOptions` | `netstandard2.0;netstandard2.1;net10.0` | `Microsoft.Extensions.Options` 10.0.0 and `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.0. |
| `Configlue.Extensions.R3` | `netstandard2.0;netstandard2.1;net10.0` | `R3` 1.3.1. |
| `Configlue.Generator` | `netstandard2.0` | `Microsoft.CodeAnalysis.CSharp` 4.11.0 and `Microsoft.CodeAnalysis.Analyzers` 3.11.0 (private analyzer dependencies). |
| `Configlue.Testing` | `netstandard2.0;netstandard2.1;net10.0` | No direct NuGet dependencies. |
| `Configlue.Provider.Json` | `netstandard2.0;netstandard2.1;net10.0` | `System.IO.Pipelines` 10.0.0; `System.Text.Json` 10.0.0 on both Standard assets. |
| `Configlue.JsonSchema.MSBuild` | `build/` and `tasks/net10.0` (no `lib` assets) | Development-only build tooling. Ships the pinned `JsonSchema.Net` generation stack privately under `tasks/net10.0`; declares no propagated NuGet dependencies. |
| `Configlue.Provider.Xml` | `netstandard2.0;netstandard2.1;net10.0` | No direct NuGet dependencies. |
| `Configlue.Provider.Yaml` | `netstandard2.0;netstandard2.1;net10.0` | `SharpYaml` 3.13.1. |
| `Configlue.Source.Environment` | `netstandard2.0;netstandard2.1;net10.0` | `System.Text.Json` 10.0.0 on both Standard assets. |
| `Configlue.Source.CommandLine` | `netstandard2.0;netstandard2.1;net10.0` | `System.CommandLine` 2.0.12. |
| `Configlue.Resource.Http` | `netstandard2.0;netstandard2.1;net10.0` | `Configlue.Provider.Json` (JSON-over-HTTP codec), `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.0, and `Microsoft.Extensions.Http` 10.0.0 for named `IHttpClientFactory` client support. |
| `Configlue.Extensions.AspNetCore` | `net10.0` | `Microsoft.AspNetCore.App` framework reference and `Configlue.Resource.Http`. |
| `Configlue.Extensions.Blazor` | `net10.0` | `Microsoft.AspNetCore.App` framework reference and `Configlue.Provider.Json` (default JSON codec for browser storage). |
| `Configlue.Resource.S3` | `netstandard2.0;netstandard2.1;net10.0` | `AWSSDK.S3` 4.0.103.4. |
| `Configlue.Source.PostgreSql` | `net8.0;net10.0` | `Npgsql` 10.0.3 and `Configlue.Provider.Json`; the backend dependency sets the `net8.0` floor. |
| `Configlue.Source.PostgreSql.Migrations` | `net8.0;net10.0` | `Npgsql` 10.0.3 and `Configlue.Source.PostgreSql`. |
| `Configlue.Resource.Redis` | `netstandard2.0;netstandard2.1;net10.0` | `StackExchange.Redis` 3.3.1. |
| `Configlue.Transformer.AES` | `netstandard2.1;net10.0` | No direct NuGet dependencies; AES-GCM requires the `netstandard2.1` floor. Install this package explicitly to enable AES extensions. |

The solution build includes generated-model consumer fixtures targeting both Standard families (`netstandard2.0` and `netstandard2.1`), both with and without the JSON provider. CI builds those fixtures with the package graph, so generator compatibility is checked independently of the generator assembly's own target.

The package references and versions in the project files are the source of truth. Install optional provider packages directly when you need them. Start with [Installation](../getting-started/installation.md).

For object storage, see [Amazon S3 object resource](./s3-object-resource.md).
For direct PostgreSQL persistence, see [PostgreSQL resource](./postgresql-resource.md).
For direct Redis persistence, see [Redis resource](./redis-resource.md).
