---
title: Design overview
description: How Resource, Source, Codec, Fragment, Patch, and Options relate.
---

# Design overview

Configlue has only six characters. Their relationship is a straight line, and so is the learning order:

```text
Resource (location) → Codec (conversion) → Source (contribution) → Fragment (diff) → Options (facade)
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
| Options | The facade apps see | Read, save, watch, explain, diagnose |

Reads flow like this: each Source fetches bytes from a Resource, a Codec turns them into a Fragment. The runtime layers only present fields by priority into one model.

Writes flow backwards: apps edit an ordinary model value. Underneath, the change becomes a Fragment diff and reaches only the Source named by `WriteRoute` or `WritePlan`. Unrelated Sources stay clean.

## Why split them

Separating location (Resource), conversion (Codec), and contribution (Source) lets each evolve alone. Switch files to HTTP, or JSON to YAML, and model read/write code stays put. Shape changes travel through versioning; location moves travel through verified copies. Details per page:

- [Resource](./resource.md): physical endpoints — files, sections, ZIP, HTTP, memory.
- [Source](./source.md): logical contributions — priority, fallback, projection, mounting.
- [Codec](./codec.md): bytes-to-values conversion and per-format notes.
- [Fragment and Patch](./fragment-patch.md): sparse diffs and single-field edits.
- [Options](./options.md): the read/write facade — profiles, dynamic options, DI adapters.

Stricter boundaries and implementation notes remain in the [design notes](../reference/design-notes.md).
