# MessagePack NativeAOT: supported vs dynamic paths (#251)

Follow-up to #208, which closed with a JSON-only warning-clean gate. The
`native-aot-supported` packed-package consumer now executes generated JSON
**and** generated MessagePack fragment round trips (nested fragments,
nullable/value types, enums, generic collections) under NativeAOT.

## Supported (warning-clean) path

- Options come from
  `ConfiglueMessagePackResolver.CreateClosedWorldOptions(...)`: generated
  Configlue fragment formatters first, then caller-supplied leaf resolvers.
  The factory never references `StandardResolver` or
  `MessagePackSerializerOptions.Standard`.
- Every leaf member type needs an explicit formatter: the application's
  MessagePack 3.x generated resolver for `[MessagePackObject]` types,
  concrete primitive formatters (`Int32Formatter`, `NullableStringFormatter`,
  `NullableInt32Formatter`, ...), one `ListFormatter<T>` per collection type,
  and `MessagePackEnumFormatter<TEnum>` (int-backed) per enum. A missing leaf
  fails fast with `FormatterNotRegisteredException` instead of falling back to
  runtime code generation.
- Sources use `FromGeneratedMessagePackFile` with those options;
  `MessagePackStateCodec<T>(options)` / `MessagePackStateValueSerializer<T>(options)`
  take the same options. Generated fragment formatters resolve leaves via
  `FormatterResolverExtensions.GetFormatterWithVerify` on the configured
  resolver and never call `MessagePackSerializer.Serialize/Deserialize`.
- Selection is unambiguous: every reflection-capable member (parameterless
  constructors, `ConfiglueMessagePackResolver.Instance`, default options,
  `FromMessagePackFile` without options) carries `RequiresDynamicCode`, so
  touching the dynamic path from AOT code fails the build.

## Dynamic (non-AOT) path

The parameterless APIs keep the general-purpose behavior for JIT hosts:
`StandardResolver`-based fallbacks, `MessagePackSerializer` static entry
points, and runtime-resolved codecs. They are intentionally excluded from the
supported consumer; the diagnostic `native-aot` broad consumer remains as a
compatibility probe for them.

## Proven residual: two upstream warnings

After eliminating every reachable dynamic edge (verified by IL-level
call-graph analysis of MessagePack 3.1.11), exactly two ILC diagnostics
remain, both on
`MessagePackSecurity.ObjectFallbackEqualityComparer.GetHashCode`:

- `new MessagePackSerializerOptions(resolver)` initializes
  `Security` from the `MessagePackSecurity.TrustedData` static, whose
  constructor unconditionally instantiates the fallback comparer, so ILC
  analyzes its `MakeGenericMethod` body.
- The method is provably never invoked from the closed-world path: its only
  callers are dictionary/set formatter `GetEqualityComparer` paths, none of
  which are referenced. ILC analyzes it only because the type is instantiated.

The release gate (`assert-supported-aot-diagnostics.sh`) therefore asserts the
exact member instead of the diagnostic code: no warning-code or namespace
allowlist exists, and `RequiresDynamicCode` injected into either supported
serialization path still fails the gate (IL3050 stays an error everywhere
else). Upgrading past MessagePack 3.1.11 should re-verify this residual; it
is the only known blocker to a strict zero-diagnostic gate.

## Adjacent fixes the gate required

- The shared Patch JSON helper (`__EffectiveOptions`) created a
  `DefaultJsonTypeInfoResolver` fallback with no suppression or reflection
  guard; it now mirrors the provider pattern (reflection-disabled guard plus
  `IL2026`/`IL3050` suppressions).
- Fragment value comparison closed generic helpers over runtime collection
  element types via `MakeGenericMethod`. The native delegates are now created
  (and cached, preserving the non-allocating steady state) only when dynamic
  code is supported; trimming/NativeAOT callers use the structural comparisons.
