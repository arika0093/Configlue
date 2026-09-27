---
title: "Design: Resource"
description: Where bytes live. Files, sections, ZIP, HTTP, memory.
---

# Design: Resource (location)

A Resource says only where bytes are. It knows nothing about value semantics or which fields use it. The kinds:

## Files

`FileResource` is the center: atomic writes with backup generations. One `.bak` generation by default; `FileResourceOptions` changes generations and their directory. `RestoreLatestBackupAsync` brings back the newest one. See [backups and observability](../advanced/backups-and-observability.md).

## Sections

A view over part of a file. `JsonSectionResource` treats a nested path like `App:Policy` as an independent Resource while preserving siblings on writes. XML elements and YAML mappings have equivalent views. Disjoint sections over one file batch into a single physical write.

Note: section writes re-serialize the document. JSONC comments, whitespace, and quoting styles are not preserved. Use standard JSON or UTF-8 YAML.

## ZIP, HTTP, memory

- `ZipEntryResource` exposes one archive entry as a logical Resource, keeping the archive's physical identity and revision. Untouched entries survive; disjoint updates batch into one archive write.
- `HttpResourceReader` reads from `{root}/get`, with ETag conditional writes and polling. Writes apply only with `Writable = true`. Serve it from ASP.NET Core with `Configlue.Resource.Http.AspNetCore`.
- `InMemoryResource` is the test double. Exercise resolution, writes, and watchers without the file system (`Configlue.Testing`).

## The ResourceId promise

Each logical Source can publish its physical Resource's `ResourceId`. Sections, ZIP entries, and projections preserve that identity so later write coordination can batch logical updates sharing a location. Backends that cannot batch a shared Resource fail before any grouped write.
