---
title: "Design: Fragment and Patch"
description: Sparse diffs and single-field edits. How presence is preserved.
---

# Design: Fragment and Patch (diffs and edits)

Fragment and Patch are the ground that resolution, migration, projection, and write planning move on. App code touches ordinary model values; these two carry the diffs underneath.

## Fragment: keeping "only what exists"

A generated Fragment remembers each model member's presence. The key point: "member missing" stays distinct from "present `null` or default". Layering never lets "unset" overwrite "set to default".

Resolution runs on Fragments: of every Source's contributed Fragment, only present members compose by priority into one model. Migration runs on Fragments too: `Fragment.FromPrevious` copies same-name, type-compatible members across versions, leaving only renames explicit.

## Patch: editing one field

The generated `TModel.Patch` is a single-field edit fragment. `ApplyPatchAsync` edits one member; explicit destinations use `StateSourcePatch` entries with `ApplyPatchesAsync` for split writes across Sources. `Unset` removes only the write Source's contribution.

## Merge behavior per member

Per-member composition habits change with `[ConfiglueMerge]`: built-in `Append`, `Deep`, `Replace`, `SetUnion`, plus custom strategy types. Collection layering and ordering semantics are decided here. See [resolution and merge](../layering/resolution-and-merge.md).
