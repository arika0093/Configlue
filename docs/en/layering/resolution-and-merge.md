---
title: Resolution and merge
description: Priority reads, presence, merge modes, and provenance explanations.
---

Register generated model options with a prioritized state-source set. Reads merge the present members from each source, and writes can target a source independently of read priority.

## Source precedence

Precedence is deterministic and part of the public contract. Sources are ordered by descending numeric `Priority`, so a higher number wins. Sources with equal priority keep registration order, and the earlier registered source wins the tie. Resolution considers members rather than whole sources: a missing member continues to the next source in priority order, and an `Unset` removes the selected contribution to reveal the next lower-priority source. Write routes are chosen independently of read priority, so an edit can target a lower-priority overlay while a higher-priority source keeps shadowing the value. See [Lifetime and source precedence](../../lifetime-and-precedence.md).

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

`await options.GetDetailsAsync()` returns a typed snapshot with effective values and per-source contributions from highest to lowest priority. Use it for troubleshooting layering and for surfacing "where did this come from" in settings UIs.

## Next steps

* [Write routing](./write-routing.md).
* [Mount and project](./mount-and-project.md).
