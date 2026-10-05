# SparseFragments

**Typed partial state for C#.**
Distinguish missing, null, and values. Generate fragments, merge, diff, and typed patches from ordinary POCOs at compile time.

Annotate a partial class with `[SparseFragmentModel]`, and the generator emits a typed **Fragment** — a presence-aware view where each member tracks whether it was specified — plus merge, semantic diff, and typed patch operations over that partial state. Targets netstandard2.0.

## The Problem: Missing Is Not Null

Plain C# properties cannot distinguish "the caller did not specify this member" from "the caller explicitly set it to `null`/`default`". That distinction becomes essential the moment data is layered:

* Multiple sources (files, environment variables, remote policies, user edits) each contribute *some* members, and higher-priority layers must override only what they actually set.
* An explicit `null` is a real, meaningful value that must win over a lower layer's value — while a missing member must fall through.
* Nested models and collections need their own merge rules (member-by-member? append? set-union?), not a blanket "last write wins".
* You want to compute the minimal difference between two states and apply it elsewhere as a patch.
* Mutable models must be cloneable without sharing references between copies.

Hand-writing this per model is boilerplate-heavy and error-prone, and reflection-based solutions sacrifice startup performance and AOT/trim compatibility.

### Presence in one glance

An explicitly set `null` overrides a lower layer; an unspecified member falls through. The generated typed API preserves that distinction:

```csharp
// Given the Settings model defined in Usage §2 below:
var defaults = Settings.Fragment.From(new Settings
{
    Label = "fallback",
    Child = new Child { Host = "db.local" },
});

// Explicit null is present: it overrides the lower layer.
var clearsLabel = new Settings.Fragment { Label = (string?)null };
// Nothing set: everything is missing, so the lower layer survives.
var saysNothing = new Settings.Fragment();

defaults.Merge(clearsLabel).ToModel().Label; // null (explicit null wins)
defaults.Merge(saysNothing).ToModel().Label; // "fallback" (missing falls through)
```

`Merge`, `Diff`, and typed `Patch` below are operations on this partial state — not separate features bolted together.

## What You Get: Layers Over Typed Partial State

1. **Presence-aware `Fragment`.** `Optional<T>` distinguishes *missing*, *present null*, and *present value* per member. Sparse construction (`new Settings.Fragment { ... }`) carries only what a layer actually sets.
2. **Merge, diff, and typed patch as operations on partial state.** Layered `Merge` overrides only present members; `Diff` captures the minimal delta between states; a typed `Patch` applies `Set` / `Unset` / `Unchanged` edits (including nested `SetNull`) without mutating the original.
3. **Advanced capabilities, when you need them.** Per-member merge algebra (`Replace` / `Deep` / `Append` / `SetUnion`, or custom strategies), immutable builders, structural `DeepClone`, and diagnostics such as rebase and contribution provenance stay available but secondary to the core mental model.
4. **Boundary interop as built-in.** Crossing a process boundary? Convert a typed patch to a standard RFC 6902 JSON Patch document (and back) with the built-in `FromJsonPatch` / `ToJsonPatch` bridge. In-process code never needs to think in JSON Patch terms.

Details for each layer follow in Usage; advanced and interop sections live at the end so they do not obscure the core model.

Adoption is one attribute on a partial class; the generator ships as an analyzer in the package and emits predictable, source-generated operations: plain generated C# — reflection-free core operations, no runtime code generation, no generator warmup at runtime — with automatic `IsExternalInit` emission for init-only members. Measure the representative paths locally with `SparseFragmentBenchmarks318` (see `benchmarks/README.md`).

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
| `Settings.Patch.FromJsonPatch` / `patch.ToJsonPatch` | RFC 6902 import/export bridge (built in) |
| `Fragment.FragmentJsonConverter` | `System.Text.Json` converter for the canonical fragment JSON used by the bridge |
| Fragment builder | Copies a fragment while changing only the members you touch |
| `DeepClone()` | Returns a fully independent copy of a model or fragment |

Reachable partial model types automatically receive generated Fragment/Patch APIs. Non-partial nested POCOs still participate structurally but do not expose generated nested types.

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

### 7. Build and clone (secondary helpers)

Builders and `DeepClone` work around the same partial state when you need an edited copy or an isolated graph:

```csharp
var builder = original.ToBuilder();
builder.Label = Optional<string?>.Missing;                     // copy without this member
var edited = builder.Build();

var clone = original.ToModel().DeepClone();                    // fully independent graph
clone.Child!.Count = 42;                                       // original.Child.Count is still 7
```

`DeepClone` preserves shared references and object cycles. `Fragment.From` and `Fragment.Diff` do not support cyclic object graphs: shared (non-cyclic) references are allowed, but a cycle throws `NotSupportedException` naming the member path instead of overflowing the stack.

### 8. Customize merging (advanced)

`[SparseMerge]` changes the merge rule per member. The defaults already cover the common cases, so reach for this only when a member needs its own algebra:

| MergeMode | Behavior |
| --- | --- |
| `Replace` | The higher layer's value wins (default for scalars and collections) |
| `Deep` | Recursively merge nested fragments member by member (default for nested models) |
| `Append` | Concatenate collections from lowest to highest priority |
| `SetUnion` | Combine as an insertion-ordered set union |
| `Custom` | Delegate to your own `FragmentMergeStrategy<T>` implementation |

### 9. Exchange patches as RFC 6902 JSON Patch (opt-in boundary interop)

Typed `Patch` values stay in-process. When a patch has to cross a process boundary — an HTTP PATCH endpoint, another service, or stored JSON — convert it to a standard [RFC 6902](https://datatracker.ietf.org/doc/html/rfc6902) document. The same bridge is generated for standalone `[SparseFragmentModel]` types and for Configlue `[ConfiglueModel]` types.

#### 9.1. No extra package needed

`FromJsonPatch` / `ToJsonPatch` are generated alongside `Fragment` / `Patch` and the runtime ships inside `SparseFragments` itself (Configlue models use the embedded `Configlue` runtime). The implementation has no ASP.NET dependencies.

#### 9.2. Import a JSON Patch document

`Patch.FromJsonPatch` applies an RFC 6902 document to the canonical JSON of a baseline fragment, then derives the equivalent typed semantic `Patch`:

```csharp
using System.Text;
using SparseFragments;

var baseline = new Settings.Fragment { Label = "base" };
var document = Encoding.UTF8.GetBytes(
    """[{"op":"replace","path":"/Label","value":"patched"}]""");

var patch = Settings.Patch.FromJsonPatch(
    Optional<Settings.Fragment?>.Present(baseline),
    document);

var updated = baseline.Apply(patch);
// updated.Label == "patched"
```

The baseline is presence-aware, so the mapping is exact:

* `replace` with a JSON `null` value becomes a present null; `remove` becomes absent (`Missing`).
* A nested `add` fails when its parent fragment is absent (`JsonPatchErrorKind.MissingParent`).
* Whole-contribution transitions use the root pointer `""`: `add` from `Optional<Fragment?>.Missing`, `replace` with `null`, and `remove` to absent.
* Array element operations (`/Tags/1`, `/Tags/-`) work on import; collections are whole values on export (see below).
* Pointers follow RFC 6901 (`~0` for `~`, `~1` for `/`) and honor `[JsonPropertyName]` wire names, `JsonSerializerOptions.PropertyNamingPolicy`, and `PropertyNameCaseInsensitive`.

An overload taking a present `Fragment` directly (`FromJsonPatch(baseline, document, options)`) covers the common case. If a model happens to declare members named `FromJsonPatch` / `ToJsonPatch`, the bridge is emitted with a `Sparse` prefix instead (`SparseFromJsonPatch` / `SparseToJsonPatch`).

#### 9.3. Export a typed patch

`ToJsonPatch` runs the typed patch against the same baseline and diffs the before/after canonical JSON:

```csharp
var baselineOpt = Optional<Settings.Fragment?>.Present(baseline);
var exported = patch.ToJsonPatch(baselineOpt); // ReadOnlyMemory<byte>, UTF-8 JSON
Console.WriteLine(Encoding.UTF8.GetString(exported.ToArray()));
// [{"op":"replace","path":"/Label","value":"patched"}]
```

Export is semantic, not a verbatim replay of the import:

| Input shape | Exported shape |
| --- | --- |
| Objects | Diffed recursively member by member |
| Arrays and scalars | Collapsed to a whole-value `add` / `remove` / `replace` on the member path |
| `move` / `copy` | Collapsed to the equivalent `remove` plus `add` / `replace` |
| `test` | Validation-only; never appears in the export |

Round-tripping holds semantically: applying the re-imported export to the same baseline produces the same fragment as applying the original typed patch.

#### 9.4. Options, converters, and NativeAOT

Both directions accept an optional `JsonSerializerOptions`:

```csharp
var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
};

// Scalar/collection members resolve through options.TypeInfoResolver.
var patch = Settings.Patch.FromJsonPatch(baselineOpt, document, options);
var exported = patch.ToJsonPatch(baselineOpt, options);
```

The fragment itself never needs `JsonTypeInfo` metadata: conversion goes through the generated `Fragment.FragmentJsonConverter` directly. Only scalar/collection member types use the supplied resolver, so NativeAOT applications just pass a source-generated context:

```csharp
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class PatchContext : JsonSerializerContext;
```

```csharp
var options = new JsonSerializerOptions { TypeInfoResolver = PatchContext.Default };
```

When reflection-based serialization is disabled and no resolver is supplied, the bridge fails fast with a clear `InvalidOperationException` instead of reaching runtime codegen. `Fragment.FragmentJsonConverter` is also public, so ordinary `JsonSerializer.Serialize(fragment, options)` works with the same presence semantics (present members only, explicit nulls preserved).

#### 9.5. Failures are typed

Malformed documents, unknown operations, bad pointers, missing targets/parents, invalid array indices, failed `test` operations, unmapped properties, and member deserialization failures all throw `JsonPatchException` with a machine-readable `Kind`:

```csharp
try
{
    var patch = Settings.Patch.FromJsonPatch(baselineOpt, document);
}
catch (JsonPatchException ex) when (ex.Kind == JsonPatchErrorKind.MissingTarget)
{
    // e.g. replace/remove/test on a path that does not exist in the baseline.
}
```

## Main Use Cases

All of these are uses of the same typed partial state: keep an edit, override, or delta as a `Fragment`/`Patch` that remembers what was specified, then combine it with `Merge`, `Diff`, or `Apply`.

* **Layered overlays.** Combine defaults with per-environment, per-user, or per-tenant overrides. Each layer only carries what it changes, and a priority-ordered `Merge` produces the effective state.
* **Partial-update APIs and DTO patching.** HTTP PATCH-style endpoints where "absent", "null", and "value" are three distinct intents. Keep the incoming partial update as a typed fragment and apply it onto the current state — no reflection involved.
* **Storing only user-modified settings.** `Diff` the current settings against the defaults and persist only the resulting fragment. Saved data stays minimal, and future default changes still reach users who never overrode them.
* **Edit sessions and dirty tracking.** Accumulate user edits in a `Patch`, check `IsEmpty` to know whether anything changed, apply it for a preview, or drop it to cancel. The original model is never mutated, so there is no manual restore logic to write.
* **State diffs between snapshots.** Derive `Diff(before, after)` and apply it to another in-process snapshot with `ApplyChanges`. Serialization and cross-version wire formats are separate application concerns.
* **Boundary exchange (secondary).** Accept standard JSON Patch documents at the edge with `Patch.FromJsonPatch`, work with them as typed semantic patches in-process, and send them back out with `patch.ToJsonPatch`. `test` operations validate before mutation, and the export stays minimal (recursive for objects, whole-value for arrays/scalars).
* **Safe duplication (secondary).** `DeepClone` copies models with nested and mutable members (including collections and shared references) without handwritten copy constructors.

## License

This project is licensed under the Apache-2.0 License.
