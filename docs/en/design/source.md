---
title: "Design: Source"
description: Logical contributions. Priority, fallback, projection, mounting.
---

# Design: Source (contribution)

A Source is a logical contribution: which fields, at which priority. Reading, writing, and watching are exposed independently. If a Resource is the location, a Source is how that location is used.

## Priority and fallback

When several Sources hold the same field, the larger `Priority` wins. `FallbackStateSource` groups alternate representations of one logical state (canonical JSON plus legacy YAML, say) and presents the first readable candidate as the Source — values across formats are never overlaid.

Fall-through on missing files, and surfacing other read failures, is a Source promise. Assembly details live in [files and sections](../sources/files-and-sections.md) and [environment and command line](../sources/environment-and-commandline.md).

## Read-only as a property

Environment, command-line, and default HTTP sources are read-only. Trying to change a value shadowed by a read-only contribution from the writable side fails with a conflict instead of silently ignoring it. Checking origins with `GetDetailsAsync` before saving pays off.

## Projection and mounting

Two mechanisms lend a model subtree to another Source:

- **Projection:** reshape an existing Source's values into another model, used for per-destination verification and retryable migration.
- **Mounting:** attach a separate Source at a nested model path (`AddMounted`) — for example, letting only `Policy` come from an HTTP layer. See [mount and project](../layering/mount-and-project.md).

Presets like `UseCommonSources` fold this Source assembly into standard shapes ([common sources](../basic-usage/common-sources.md)).
