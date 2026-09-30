---
title: Design overview
description: How Resource, Source, Codec, Fragment, Patch, and State relate.
---

Configlue has only six characters. Their relationship is a straight line, and so is the learning order:

```text
Resource (location) → Codec (conversion) → Source (contribution) → Fragment (diff) → State (facade)
                                                        ↘ Patch (edit fragment)
```

## In one sentence each

| Concept | In short | Example |
| --- | --- | --- |
| Resource | Where bytes live | File, ZIP entry, HTTP response, memory |
| Codec | Bytes-to-values conversion | JSON / XML / YAML reading and writing |
| Source | A logical contribution | "The `Server` part of the user settings file" |
| Fragment | A diff that remembers presence | A state with "only `Port`" |
| Patch | A single-field edit | "Set `Port` to 9000" |
| State | The facade apps use | Read, save, watch, explain, diagnose |

Reads flow like this: each Source fetches bytes from a Resource, a Codec turns them into a Fragment. The runtime layers only present fields by priority into one model.

Writes flow backwards: apps edit an ordinary model value. Underneath, the change becomes a Fragment diff and reaches only the Source named by `WriteRoute` or `WritePlan`. Unrelated Sources stay clean.

Separating location, conversion, and contribution lets each evolve alone. Switch files to HTTP, or JSON to YAML, and model read/write code stays put. Shape changes travel through versioning; location moves travel through verified copies. The [state facade](./state.md) is described separately.

## Resource: where bytes live

A Resource says only where bytes are. It knows nothing about value semantics or which fields use it.

* **Files.** `FileResource` is the center: atomic writes with backup generations. One `.bak` generation by default; `FileResourceOptions` changes generations and their directory. `RestoreLatestBackupAsync` brings back the newest one. See [backups and observability](../advanced/backups-and-observability.md).
* **Sections.** A view over part of a file. `JsonSectionResource` treats a nested path like `App:Policy` as an independent Resource while preserving siblings on writes. XML elements and YAML mappings have equivalent views. Disjoint sections over one file batch into a single physical write. JSON and YAML section writes apply edits to the original document text, preserving unrelated comments, whitespace, quoting, and scalar styles; JSON and JSONC files accept comments and trailing commas. When the codec has no structured editing support, its existing full-document replacement behavior remains unchanged.
* **ZIP, HTTP, memory.** `ZipEntryResource` exposes one archive entry as a logical Resource, keeping the archive's physical identity and revision; untouched entries survive and disjoint updates batch into one archive write. `HttpResourceReader` reads from `{root}/get`, with ETag conditional writes and polling; writes apply only with `Writable = true`, and `Configlue.Extensions.AspNetCore` serves it. `InMemoryResource` is the test double (`Configlue.Testing`).

Each logical Source can publish its physical Resource's `ResourceId`. Sections, ZIP entries, and projections preserve that identity so later write coordination can batch logical updates sharing a location. Backends that cannot batch a shared Resource fail before any grouped write.

## Codec: bytes to values

A Codec converts bytes to typed values and back, with no Resource I/O. It owns "how to read", not "where to keep".

* **JSON**: `JsonStateCodec`, combined with section resources and file registration. Pass a source-generated `JsonSerializerContext` for trimming-safe, NativeAOT-friendly behavior. JSON Schema export is provided at build time by `Configlue.JsonSchema.MSBuild`.
* **XML**: the XML codec, with section resources and file registration.
* **YAML**: the YAML codec, with section resources and file registration. See `example/Example.ConsoleApp.Yaml` for camel-case naming.
* **Document layouts**: the JSON and YAML codecs read both the simple layout (`{ "$version": 1, ... }`, the write default) and the detailed `$configlue`/`$value` envelope. Select the write layout with `DocumentLayoutOptions` on the codec or file options; legacy `Configuration.Writable` files read as simple documents. See the [adoption guide](../migration/adopting-configuration-writable.md).

### Byte transformers and state middleware

`IStateByteTransformer` transforms persisted bytes between a Resource and a Codec. Read transforms run in registration order; write transforms run in reverse. The optional `Configlue.Transformer.AES` package provides `AesGcmStateByteTransformer` for AES-GCM encryption and authentication, and classifies authentication failures as eligible for backup recovery. Manage keys securely in the application and dispose the transformer when it is no longer needed. Pass transformers to `SerializedStateSource.FromResource`.

After the Codec, `IStateMiddleware<T>` wraps typed readers and writers for auditing, validation, normalization, and similar behavior. The first registered middleware is outermost. A middleware that wraps a writer and needs batch writes must preserve `ISourceWriteBatchParticipant<T>` on its returned writer.

```csharp
using Configlue.Codecs;
using Configlue.State;
using Configlue.Sources;
using Configlue.Transformer.AES;

using var encryption = new AesGcmStateByteTransformer(key);
var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
    "remote",
    resource,
    new JsonStateCodec<AppSettings.Fragment>(),
    transformers: [encryption],
    middlewares: [new AuditMiddleware()]);
```

When in doubt, match the file format. Add one Codec per format you read, and narrow writes to one destination (the YAML-first, JSON-second guide shows a typical shape). Mixing formats changes nothing about Source priority or `WriteRoute`.

## Source: a logical contribution

A Source is a logical contribution: which fields, at which priority. Its typed read, write, and watch contracts are `Configlue.Sources.ISourceReader<T>`, `ISourceWriter<T>`, and `ISourceWatcher`. Physical resource I/O uses the separate `Configlue.Resources.IResourceReader` and `IResourceWriter` contracts. If a Resource is the location, a Source is how that location contributes to state.

* **Priority and fallback.** When several Sources hold the same field, the larger `Priority` wins. `FallbackStateSource` groups alternate representations of one logical state (canonical JSON plus legacy YAML, say) and presents the first readable candidate as the Source — values across formats are never overlaid. Fall-through on missing files, and surfacing other read failures, is a Source promise; assembly details live in [files and sections](../sources/files-and-sections.md) and [environment and command line](../sources/environment-and-commandline.md).
* **Read-only as a property.** Environment, command-line, and default HTTP sources are read-only. Trying to change a value shadowed by a read-only contribution from the writable side fails with a conflict instead of silently ignoring it. Checking origins with `GetDetailsAsync` before saving pays off.
* **Projection and mounting.** Two mechanisms lend a model subtree to another Source: **projection** reshapes an existing Source's values into another model, used for per-destination verification and retryable migration; **mounting** attaches a separate Source at a nested model path (`AddMounted`) — for example, letting only `Policy` come from an HTTP layer. See [mount and project](../layering/mount-and-project.md).

Presets like `UseCommonSources` fold this Source assembly into standard shapes ([common sources](../basic-usage/common-sources.md)).

## Fragment and Patch: diffs and edits

Fragment and Patch are the ground that resolution, migration, projection, and write planning move on. App code touches ordinary model values; these two carry the diffs underneath.

* **Fragment** remembers each model member's presence. The key point: "member missing" stays distinct from "present `null` or default". Layering never lets "unset" overwrite "set to default". Resolution runs on Fragments: of every Source's contributed Fragment, only present members compose by priority into one model. Migration runs on Fragments too: `Fragment.FromPrevious` copies same-name, type-compatible members across versions, leaving only renames explicit.
* **Patch** is the generated `TModel.Patch`, a single-field edit fragment. `SaveAsync` edits one member; explicit destinations use `StateSourcePatch` entries with `ApplyPatchesAsync` for split writes across Sources. `Unset` removes only the write Source's contribution.
* **Merge behavior per member** changes with `[ConfiglueMerge]`: built-in `Append`, `Deep`, `Replace`, `SetUnion`, plus custom strategy types. Collection layering and ordering semantics are decided here. See [resolution and merge](../layering/resolution-and-merge.md).

## Boundaries, project map, and status

A **resource** represents a physical endpoint such as a file, a ZIP entry, or an HTTP response. A **codec** translates bytes to and from typed values without performing resource I/O. A **source** contributes a logical configuration snapshot and can independently expose read, write, and watch capabilities. A generated **fragment** preserves whether each model member is missing or present, including a present `null` or default value. Resolution, migration, projection, and write planning operate on fragments; application code edits ordinary model values.

Projects live directly under `src/`. `Configlue` is a no-assembly meta-package that brings in Core, the Microsoft dependency-injection integration, the JSON provider, HTTP resources with named-`IHttpClientFactory` support, common layered sources, the environment source, and the source-generator analyzer. `Configlue.Abstraction` holds the contracts; `Configlue.Core` holds the framework-neutral resolution, builder/context, and persistence runtime including the general file resource, and is usable without a dependency-injection container; `Configlue.Extensibility` is the provider SDK for serialization, transformed resources, and mounted source registration. `Configlue.Extensions.DI` provides Microsoft dependency-injection registration for generated state, profiles, per-subject host integration, and scoped/keyed lifetimes, and optional Microsoft options adapters are in `Configlue.Extensions.MSOptions`. `Configlue.Generator` emits sparse fragments and patches. `Configlue.Provider.Json`, `.Xml`, and `.Yaml` contain format codecs, section resources, and file registrations; `Configlue.JsonSchema.MSBuild` generates JSON Schemas at build time; `Configlue.Source.Environment`, `.CommandLine`, and `.Presets` provide sources and the standard layered preset (with optional `.Presets.Yaml` and `.Presets.Xml` adapters). `Configlue.Resource.Http`, `.S3`, and `.Zip` cover transport and storage; host integrations live in `Configlue.Extensions.AspNetCore` (request subjects and HTTP resource endpoints) and `Configlue.Extensions.Blazor` (authentication-state subjects and browser storage); `Configlue.Testing` provides in-memory doubles.

The foundation is in place: backend-neutral read/write/watch contracts, prioritized resolution, schema and storage migration, projections, provenance details, topology diagnostics, optional structured logging, section and ZIP resources, backup rotation and restore, generated sparse fragments, format codecs, and JSON Schema export. The current API is an architectural foundation rather than a feature-complete replacement for Configuration.Writable.

Known limitations:

* Writes across different resources are not atomic.
* Source retirement is scoped to the current state instance and leaves backing data intact; callers must update source registration for future process starts.
* A source set is fixed for a state runtime. Dynamic named states and persistent profiles can create or remove whole runtimes, each with its own source set.
* `FileStateStorageMigrationJournal` holds a cross-process lease for the full run of a migration ID. Custom journals that do not implement `IStateStorageMigrationLeaseProvider` require callers to coordinate concurrent runs.
* Watchers provide invalidation signals; provider-specific polling, retry, and reconnection policies remain the provider's responsibility.

Configlue does not replace the source set of an existing state identity in place. Build a new context, migrate explicitly when required, switch the application's consumers, and dispose the old context. See [dynamic states](../profiles/dynamic-states.md) for the current per-name lifecycle.
