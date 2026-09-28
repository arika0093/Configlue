---
title: Packages
description: Find the NuGet package for each Configlue capability.
---

# Packages

| Package | Purpose |
| --- | --- |
| `Configlue` | User-facing meta-package: Core, DI integration, JSON provider, HTTP resources, common layered sources, environment source, and the generator analyzer. Contains no implementation assembly of its own. |
| `Configlue.Abstraction` | Provider, codec, resource, and generated-model contracts. |
| `Configlue.Core` | State resolution and persistence runtime. |
| `Configlue.Extensions.DI` | Dependency-injection registration for Configlue options. |
| `Configlue.Extensions.MSOptions` | Optional adapters for Microsoft's options interfaces. |
| `Configlue.Generator` | Generated sparse model support (Roslyn analyzer). |
| `Configlue.Testing` | In-memory resources and test doubles. |
| `Configlue.Provider.Json` | JSON codec, section resources, file registrations, and JSON Schema export. |
| `Configlue.Provider.Xml` | XML codec with section resources and file registrations. |
| `Configlue.Provider.Yaml` | YAML codec with section resources and file registrations. |
| `Configlue.Source.Environment` | Read-only source backed by process environment variables. |
| `Configlue.Source.CommandLine` | Read-only source backed by a `System.CommandLine` parse result, with opt-in common preset integration. |
| `Configlue.Source.Common` | Global/local/file/environment source presets, included by the `Configlue` meta-package. |
| `Configlue.Resource.Http` | HTTP read/write resources with ETag revisions and polling change detection. |
| `Configlue.Resource.Http.AspNetCore` | ASP.NET Core endpoints for serving HTTP resources. |
| `Configlue.Resource.Dapr` | Optional byte resources and source registration for Dapr State Management. |
| `Configlue.Resource.Zip` | Resource view over one entry in a ZIP archive. |

The package references and versions in the project files are the source of truth. Install optional provider packages directly when you need them. Start with [Installation](../getting-started/installation.md).

For Dapr state persistence and the object-storage provider boundary, see [Dapr State Management resource](./dapr-state-resource.md).
