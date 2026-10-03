# SparseFragments

*Presence-aware merge, diff/patch, and deep clone for plain C# models — generated at compile time.*

Annotate a partial class with `[SparseFragmentModel]`, and the generator emits a typed **Fragment** (each member tracks whether it was specified), a **Patch**, semantic **Diff**, layered **Merge**, and **DeepClone**. Targets netstandard2.0.

## The Problem

Plain C# properties cannot distinguish "the caller did not specify this member" from "the caller explicitly set it to `null`/`default`". That distinction becomes essential the moment data is layered:

* Multiple sources (files, environment variables, remote policies, user edits) each contribute *some* members, and higher-priority layers must override only what they actually set.
* An explicit `null` is a real, meaningful value that must win over a lower layer's value — while a missing member must fall through.
* Nested models and collections need their own merge rules (member-by-member? append? set-union?), not a blanket "last write wins".
* You want to compute the minimal difference between two states and apply it elsewhere as a patch.
* Mutable models must be cloneable without sharing references between copies.

Hand-writing this per model is boilerplate-heavy and error-prone, and reflection-based solutions sacrifice startup performance and AOT/trim compatibility.

## What You Get

* **Nearly zero adoption effort.** One attribute on a partial class; everything else is generated.
* **Hand-written-grade performance.** Plain generated C# — no reflection, no runtime codegen, no warmup.
* **Exact presence semantics.** `Optional<T>` distinguishes *missing*, *present null*, and *present default*.
* **Typed, source-generated API.** `Fragment`, `Patch`, and a builder per model, all compiler-checked.
* **Per-member merge algebra.** `Replace` / `Deep` / `Append` / `SetUnion`, or custom strategies via `[SparseMerge]`.
* **Semantic diff and patch.** Minimal deltas between states, applied with `Set` / `Unset` / `Unchanged`, including nested `SetNull`.
* **Immutable edits with structural isolation.** Originals never mutate; `DeepClone` is fully independent.
* **Reflection-free metadata.** Generated schema descriptors enumerate present members for serializers and tooling.
* **Legacy-friendly.** Generator ships as an analyzer in the package; automatic `IsExternalInit` emission for init-only members.

## Usage

### 1. Install the package

```shell
dotnet add package SparseFragments
```

The generator ships inside the package as an analyzer, so this is the only setup step. From then on, all the supporting code is generated for you at compile time.

### 2. Define your model

All you need is `[SparseFragmentModel]` on a `partial` class:

```csharp
using SparseFragments;

[SparseFragmentModel]
public partial class Settings
{
    public string? Label { get; set; }
    public Child? Child { get; set; }

    [SparseMerge(MergeMode.Append)]
    public IReadOnlyList<string> Plugins { get; set; } = [];
}

public partial class Child
{
    public int Count { get; set; }
    public string Host { get; set; } = "localhost";
}
```

Once you build, the generator adds the following members inside your model type:

| Generated member | Purpose |
| --- | --- |
| `Settings.Fragment` | A sparse, presence-aware view shaped like your model |
| `Settings.Patch` | Member-level mutation directives (Set / Unset / unchanged) |
| Fragment builder | Copies a fragment while changing only the members you touch |
| `DeepClone()` | Returns a fully independent copy of a model or fragment |

Decorate a nested type (like `Child` above) when you want to construct its Fragment/Patch types directly in your code, as this example does. Nested POCOs participate in deep merge, diff, patch, and clone even *without* the attribute — but their generated fragment types get internal names and cannot be written directly.

### 3. Background: the three states of `Optional<T>`

At the heart of SparseFragments is `Optional<T>`:

```csharp
Optional<string?> a = Optional<string?>.Missing;       // not specified (IsPresent == false)
Optional<string?> b = "hello";                         // present (implicit conversion)
Optional<string?> c = Optional<string?>.Present(null); // explicitly null
```

"Missing" is treated as different from "holding null/default". During a merge, this information alone decides whether a lower layer's value survives or gets overridden.

### 4. Create fragments

There are two ways to create a fragment:

```csharp
// (a) From a whole model — every member becomes present
var full = Settings.Fragment.From(new Settings { Label = "base" });

// (b) Sparse construction — only the members you set become present
var sparse = new Settings.Fragment { Label = "base" };
sparse.IsEmpty; // false
```

Option (b) is the core of SparseFragments: a minimal *contribution* whose unspecified members can fall through to any number of lower layers.

### 5. Merge layered contributions

`Merge` overlays a higher-priority fragment onto a lower-priority one. Only members that are *present* in the higher layer override; *missing* members keep the lower layer's values.

```csharp
// A full model as the base layer.
var lower = Settings.Fragment.From(new Settings
{
    Label = "base",
    Child = new Child { Host = "db.local" },
    Plugins = ["base-plugin"],
});
// A sparse fragment carrying only what this layer overrides.
var higher = new Settings.Fragment
{
    Child = new Child.Fragment { Count = 9 },          // Host falls through to the lower layer
    Plugins = new[] { "extra-plugin" },                // plain values convert implicitly
};

var merged = lower.Merge(higher).ToModel();
// merged.Label       == "base"      (unset above → lower value survives)
// merged.Child.Host  == "db.local"  (nested fragments merge member by member)
// merged.Child.Count == 9           (higher priority wins)
// merged.Plugins     == ["base-plugin", "extra-plugin"]  (Append concatenates)
```

### 6. Diff and patch

`Diff` captures the minimal delta between two states; a `Patch` represents "changes to apply to one layer".

```csharp
var before = new Settings { Label = "before" };
var after = new Settings { Label = "after" };

var diff = Settings.Fragment.Diff(before, after);              // minimal semantic delta
var result = Settings.Fragment.From(before).ApplyChanges(diff);
// result.Label == "after"

var original = Settings.Fragment.From(new Settings
{
    Label = "original",
    Child = new Child { Count = 7, Host = "keep" },
});

var patch = new Settings.Patch { Label = (string?)null };      // explicit null (stays present)
patch.Child.Count = 9;                                         // typed nested set
var updated = original.Apply(patch);
// original is untouched; updated.Child.Host keeps "keep".

var remove = new Settings.Patch();
remove.Child.Unset();                                          // drop this layer's contribution
var toNull = new Settings.Patch();
toNull.Child.SetNull();                                        // explicit null, beats lower layers
```

`Patch.IsEmpty` tells you at a glance whether the patch changes anything at all.

### 7. Build and clone

```csharp
var builder = original.ToBuilder();
builder.Label = Optional<string?>.Missing;                     // copy without this member
var edited = builder.Build();

var clone = original.ToModel().DeepClone();                    // fully independent graph
clone.Child!.Count = 42;                                       // original.Child.Count is still 7
```

`DeepClone` preserves shared references and object cycles. `Fragment.From` and `Fragment.Diff` do not support cyclic object graphs: shared (non-cyclic) references are allowed, but a cycle throws `NotSupportedException` naming the member path instead of overflowing the stack.

### 8. Customize merging

`[SparseMerge]` changes the merge rule per member:

| MergeMode | Behavior |
| --- | --- |
| `Replace` | The higher layer's value wins (default for scalars and collections) |
| `Deep` | Recursively merge nested fragments member by member (default for nested models) |
| `Append` | Concatenate collections from lowest to highest priority |
| `SetUnion` | Combine as an insertion-ordered set union |
| `Custom` | Delegate to your own `FragmentMergeStrategy<T>` implementation |

## Main Use Cases

* **Layered configuration and overlay models.** Combine defaults with per-environment, per-user, or per-tenant overrides. Each layer only carries what it changes, and a priority-ordered `Merge` produces the effective state — the very workload this algebra was [extracted from Configlue](https://github.com/arika0093/Configlue/issues/61) for.
* **Partial-update APIs and DTO patching.** HTTP PATCH / JSON Merge Patch-style endpoints where "absent", "null", and "value" must be handled as three distinct intents. Keep the incoming partial update as a typed fragment and `ApplyChanges` it onto the current state — no reflection involved.
* **Storing only user-modified settings.** `Diff` the current settings against the defaults and persist only the resulting fragment. Saved data stays minimal, and future default changes still reach users who never overrode them.
* **Edit sessions and dirty tracking.** Accumulate user edits in a `Patch`, check `IsEmpty` to know whether anything changed, apply it for a preview, or drop it to cancel. The original model is never mutated, so there is no manual restore logic to write.
* **State diffs between snapshots.** Derive `Diff(before, after)` and apply it to another in-process snapshot with `ApplyChanges`. Serialization and cross-version wire formats are separate application concerns.
* **Safe duplication of rich models.** `DeepClone` copies models with nested and mutable members (including collections and shared references) without handwritten copy constructors.

## License

This project is licensed under the Apache-2.0 License.
