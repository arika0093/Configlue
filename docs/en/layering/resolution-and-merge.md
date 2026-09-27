---
title: Resolution and merge
description: Priority reads, presence, merge modes, and provenance explanations.
---

# Resolution and merge

Register generated model options with a prioritized state-source set. Reads merge the present members from each source, and writes can target a source independently of read priority.

## Presence and merge modes

Generated members keep presence explicit. A `Fragment` exposes each member as `Optional<T>` (`IsPresent` distinguishes a missing member from a present `null` or default), `ToBuilder()` returns a mutable `FragmentBuilder`, and `Fragment.FromPrevious` copies same-name, type-compatible members across declared versions.

Control per-member composition with `[ConfiglueMerge]`:

```csharp
[ConfiglueModel("app-settings", Version = 2)]
public partial class AppSettings
{
    public bool Enabled { get; set; } = true;

    [ConfiglueMerge(MergeMode.Append)]
    public IReadOnlyList<string> Plugins { get; set; } = [];
}
```

* `Replace` — higher priority wins entirely.
* `Deep` — nested members merge recursively.
* `Append` — ordered collections concatenate from low to high priority and retain duplicates. The generator rejects set-typed members because sets cannot preserve duplicates or sequence order.
* `SetUnion` — collections combine in low-to-high order and retain the first equal element. Arrays/lists preserve that order; set types have unspecified enumeration order. Edits are rebased onto each target source's collection segment.

Edits that require changing values owned by another source or are hidden by a higher-priority source fail with `StateConflictException` instead of being silently replaced. Ordinary editing remains natural C# on the model type; the generated member proxies (`builder.Value = 123`, `builder.Value.Set(123)`, `builder.Value.Unset()`, `builder.Value.CopyFrom(...)`) are the advanced, source-local surface.

The generated member names, the `Optional<T>`/`FragmentOperation<T>` shapes, presence semantics, and `Fragment.FromPrevious`/`CreateSchemaDispatcher` behavior are the stable public contract; the exact layout of emitted helper code may change.

## Provenance

`IConfiglueOptions<T>.ExplainAsync("Database.Host")` returns the effective value and the present source contributions from highest to lowest priority. Use it for troubleshooting layering and for surfacing "where did this come from" in settings UIs.

## Next steps

* [Write routing](./write-routing.md).
* [Mount and project](./mount-and-project.md).
