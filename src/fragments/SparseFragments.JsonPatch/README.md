# SparseFragments.JsonPatch

*RFC 6902 JSON Patch for presence-aware partial models — no ASP.NET dependencies.*

`SparseFragments.JsonPatch` is the JSON Patch runtime behind the `Patch.FromJsonPatch` / `patch.ToJsonPatch` bridges generated for `[SparseFragmentModel]` types. It parses RFC 6902 documents, applies them atomically to a baseline JSON state, and diffs two JSON states into a minimal, semantically equivalent patch document.

## When to use this package

* You already use `SparseFragments` and want to accept standard JSON Patch documents at an HTTP PATCH boundary, then work with them as typed semantic patches in-process.
* You need a small, dependency-free RFC 6902 engine over `System.Text.Json.Nodes`: atomic `Apply` (all operations succeed or none take effect), plus `Diff` for minimal `add` / `remove` / `replace` documents.
* You run on NativeAOT / trimmed applications: the engine works directly on `JsonNode` without runtime codegen or ASP.NET dependencies.

If you only need typed in-process merge, diff, patch, and clone without JSON Patch wire interop, the `SparseFragments` package alone is enough.

## Usage

```csharp
using System.Text.Json.Nodes;
using SparseFragments.JsonPatch;

// Parse a standard RFC 6902 document.
var document = SparseJsonPatch.Parse(
    """[{"op":"replace","path":"/Label","value":"patched"}]""");

// Apply it atomically to a baseline JSON state.
JsonNode? baseline = JsonNode.Parse("""{"Label":"base"}""");
var applied = SparseJsonPatch.Apply(baseline, baselineIsAbsent: false, document);
// applied.Node -> {"Label":"patched"}; failures throw JsonPatchException.

// Diff two JSON states into a minimal patch document.
JsonNode? before = JsonNode.Parse("""{"Label":"before"}""");
JsonNode? after = JsonNode.Parse("""{"Label":"after"}""");
var diff = SparseJsonPatch.Diff(before, beforeIsAbsent: false, after, afterIsAbsent: false);
byte[] utf8 = SparseJsonPatch.Serialize(diff);
```

Generated models build on these primitives: `Patch.FromJsonPatch` maps an RFC 6902 document onto the canonical JSON of a baseline fragment (so `replace` with JSON `null` becomes a present null while `remove` becomes absent), and `patch.ToJsonPatch` diffs the before/after canonical JSON. `test` operations validate before mutation, and pointers follow RFC 6901 (`~0` for `~`, `~1` for `/`).

## Failure model

Malformed documents, unknown operations, bad pointers, missing targets/parents, invalid array indices, failed `test` operations, and `JsonNode` conversion failures throw `JsonPatchException` with a machine-readable `JsonPatchErrorKind`.

## License

This project is licensed under the Apache-2.0 License.
