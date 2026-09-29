---
title: Packages
description: Find the NuGet package for each Configlue capability.
---

| Package | Purpose |
| --- | --- |
| `Configlue` | Batteries-included package: common layered and single-binary presets plus Core, DI integration, JSON provider, JSON Schema export, HTTP resources, environment source, AES helpers, and the generator analyzer. |
| `Configlue.Abstraction` | Provider, codec, resource, and generated-model contracts. |
| `Configlue.Core` | Serializer-neutral state resolution runtime, provider-authoring helpers, ZIP resources, and common file-preset SPI. |
| `Configlue.Extensions.DI` | Dependency-injection registration for Configlue options. |
| `Configlue.Extensions.MSOptions` | Optional adapters for Microsoft's options interfaces. |
| `Configlue.Extensions.R3` | Optional R3 observables for composing values, profiles, and reload signals. |
| `Configlue.Generator` | Generated sparse model support (Roslyn analyzer). |
| `Configlue.Testing` | In-memory resources and test doubles. |
| `Configlue.Provider.Json` | JSON codec, section resources, file registrations, and common-preset JSON selection. |
| `Configlue.JsonSchema` | JSON Schema generation and export for Configlue models; included by the `Configlue` meta-package. |
| `Configlue.Provider.Xml` | XML codec with section resources, file registrations, and common-preset XML selection. |
| `Configlue.Provider.Yaml` | YAML codec with section resources, file registrations, and common-preset YAML selection. |
| `Configlue.Source.Environment` | Read-only source backed by process environment variables. |
| `Configlue.Source.CommandLine` | Read-only source backed by a `System.CommandLine` parse result, with opt-in common preset integration. |
| `Configlue.Resource.Http` | HTTP read/write resources with ETag revisions and polling change detection. |
| `Configlue.Resource.Http.AspNetCore` | ASP.NET Core endpoints for serving HTTP resources. |
| `Configlue.Resource.Dapr` | Optional byte resources and source registration for Dapr State Management. |
| `Configlue.Resource.S3` | Optional Amazon S3 object resources and source registration with ETag revisions. |
| `Configlue.Transformer.AES` | AES-GCM encryption and authentication for state bytes between a Resource and Codec, including passphrase-based key derivation. |

The package references and versions in the project files are the source of truth. Install optional provider packages directly when you need them. Start with [Installation](../getting-started/installation.md).

For Dapr state persistence and object storage, see [Dapr State Management resource](./dapr-state-resource.md) and [Amazon S3 object resource](./s3-object-resource.md).
