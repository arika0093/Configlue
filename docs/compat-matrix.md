# Compiler/Runtime Compatibility Matrix

Product-level compatibility contract for Configlue generators and runtimes.
Authoritative for issue #281: every legacy shim and compatibility test below maps
to a row in this matrix. Anything without a row was removed.

## 1. Supported matrix

| Area | Minimum supported | Notes |
| --- | --- | --- |
| Primary runtime / TFM | `net10.0` | Full test suite executes here. |
| Secondary modern TFMs | `net8.0` | Godot (`GodotSharp` 4.7.2), Avalonia, PostgreSQL hosts only. |
| Portable consumption TFMs | `netstandard2.0`, `netstandard2.1` | Consumed via NuGet assets; `netstandard2.1` is the Unity 6 target. |
| .NET Framework | `net48`, compile-consumption only | Libraries ship `netstandard2.0` assets consumed by net48 apps; WPF/WinForms ship `net48` assets. No full suite re-execution on net48 (see §5). |
| Compiler language version for consuming generated code | C# 9 | Unity 6 ceiling (Unity Editor ships Roslyn/C# 9.0). Generated code uses `get; init;` plus an emitted `IsExternalInit` polyfill; no `ModuleInitializer`, file-local types, or `required` members in emitted code. |
| Roslyn host / analyzer baseline | `Microsoft.CodeAnalysis.CSharp` 4.3.1 | **Kept, not raised.** Unity 6 mandates Roslyn 4.3 generator/host compatibility ("Your source generator must use Microsoft.CodeAnalysis.CSharp 4.3 to work with Unity"). Both generators target `netstandard2.0` + Roslyn 4.3.1 for the same reason. |
| Unity | Unity 6 (6000.x), `netstandard2.1`, C# 9 | Adapter contract tests use a `UnityEngine.CoreModule` test double and validate adapter behavior only, not Unity binary/runtime compatibility. |
| Godot | `GodotSharp` 4.7.2, `net8.0`/`net10.0` | Modern SDK/Roslyn; no legacy shims. |
| Desktop Windows hosts | `net10.0-windows`, `net48` (WPF/WinForms), WinUI/MAUI on `net10.0` | `net48` desktop assets are compile targets; runtime gates run on modern Windows. |

## 2. Roslyn 4.3.1 baseline decision (kept)

Raising the generator baseline (e.g. to Roslyn 4.8+/C# 10+) would simplify
generated code, but Unity 6 editors load generators against Roslyn 4.3 and
compile user code as C# 9. Raising the baseline without a Unity-side upgrade
would break the only host that pins an old compiler, so 4.3.1 stays until
Unity documents a newer supported Roslyn. `RoslynSymbolCompat` (syntax +
`RequiredMemberAttribute` name matching instead of new symbol APIs) exists so
the 4.3.1-built generator still understands `required` members authored in
modern IDEs without requiring a newer host.

## 3. Shim map

| Shim | Maps to | Status |
| --- | --- | --- |
| `ConfiglueGenerator` / `SparseFragmentsGenerator` `IsExternalInit` emission + `ConfiglueEmitIsExternalInit` / `SparseFragmentsEmitIsExternalInit` props | Unity 6 / `netstandard2.0` / net48 compilations lacking the BCL marker | Kept. Proven by `IsExternalInit*` host tests. |
| `RoslynSymbolCompat.IsRequired` | C# 9/older hosts + `required` members authored on modern SDKs | Kept. Proven by `RequiredMember_IsUnderstoodOnRoslyn431Host` (4.3.1-built generator run with Preview parse + emit). |
| `SparseJsonPatchEmitter` `#if NET5_0_OR_GREATER` guard around `UnconditionalSuppressMessage` | Modern-only trim/AOT attributes; `netstandard2.0`/`netstandard2.1`/net48 cannot resolve them | Kept. |
| `#if NETSTANDARD` / `#if NETSTANDARD2_0` branches in `src/` (hashing, file resources, host paths, SSM, Xml codec proxies) | `netstandard2.x` consumption assets incl. Unity 6 (`netstandard2.1`) and net48-via-`netstandard2.0` | Kept. None is net48-execution-only, so none was removed. |
| `tests/Configlue.Tests/Compatibility/ReadOnlySetSmokeShim.cs` (`IReadOnlySet<T>` for `NET48 || NETSTANDARD2_0`) | BCL gap: neither TFM ships `IReadOnlySet<T>`; the smoke consumer needs the identity so the generator recognizes set members | Kept, widened from net48-only now that the `netstandard2.0` smoke compiles the same portable set surface. Mirrors the `set-clones` / `package-sparse-netstandard` consumer shims. Never ships in a package. |

## 4. Test map

| Suite | Proves | Status |
| --- | --- | --- |
| `GeneratorHostCompatibilityTests` (Roslyn 4.3.1 host run, C# 9 emit, C# 9 standalone compile, MessagePack emit on old host, `IsExternalInit` shim mapping, `required`-member host proof) | Unity 6 host contract | Kept minimal; exhaustive diagnostic/behavior matrices removed without transfer — intentional, not host-mapped (see §6). Runtime clone/merge/patch behavior stays covered in `SparseFragments.Tests` and `Configlue.Tests` contracts. |
| `Configlue.Tests` `netstandard2.0`/`netstandard2.1` Portable smoke (`IsTestProject=false`) | Compile-consumption of portable surface incl. generator execution | Kept. Build-only; runtime is not executed on these TFMs. |
| `Configlue.Tests` `net48` Portable smoke | Same as above for the net48 consumer | Replaces full net48 suite re-execution (§5). Build-only; net48 runtime is unverified. |
| `package-sparse-netstandard` consumer (`netstandard2.0`/`net48` build, no built-in `IsExternalInit`) | Packed-generator consumption without BCL marker | Kept build-only (CI `test-package-consumers.yaml` builds, never executes). |
| `set-clones` consumer (`netstandard2.0`/`net48`/`net10.0`) | Build-only consumption check incl. generator execution; runtime execution is manual-only, never CI-gated (net48 runtime unverified, see §5) | Kept as manual build check. Manual runtime steps live in its README. |
| `Configlue.Hosting.Unity.Tests` | Unity adapter behavior against engine test double | Kept. |

## 5. net48 policy

net48 is a compile-consumption target, not a runtime matrix target: the suite
executes on `net10.0`, while net48 builds the Portable smoke (the generator
still executes at compile time, so generation failures fail the build).
net48 runtime execution is unverified and not claimed: no suite, consumer exe,
or Portable smoke executes on the .NET Framework runtime in CI. The net48
Portable smoke, the `package-sparse-netstandard` net48 asset, and the
`set-clones` net48/`netstandard2.0` assets are build-only; their runtime
behavior is not executed in CI (the `set-clones` README steps are manual-only
ad-hoc verification on Windows). Verified runtime stays on `net10.0` (plus
`net8.0` for the hosts in §1).
Removed with #281: the `net48` full-suite re-execution in
`test-strict.yaml`, the `net48` `Compile Remove` exclusion list and `Exe`
output in `Configlue.Tests.csproj`, the `#if NET48` AWS-SDK sync stubs, the
`#if NETFRAMEWORK` string-compare branch, and the `#if !NET48` AES/hosting
exclusions (dead once net48 no longer compiles those files).
The AWS-SDK surface is async-only: the removed `#if NET48` sync stubs have no
replacement because net48 no longer compiles the SecretsManager resource;
async APIs remain the single supported surface on the executed TFMs.

## 6. Removed diagnostic matrices (intentionally unasserted, not transferred)

The `GeneratorHostCompatibilityTests` exhaustive matrices removed with #281
assert generator diagnostic IDs, not host-specific behavior, so they were
removed without a transfer destination. The IDs still exist in `src/` and are
still emitted; only the compat-suite assertions were dropped. This is an
intentional abolition, not a move: re-add a diagnostic-ID test only with a
host-mapped justification. Runtime semantics stay covered by behavior
contracts on `net10.0` (no diagnostic-ID assertions):

- `UnsupportedMutableCloneShapesRequireExplicitReferenceSafePolicy` /
  `ConstructorBoundReferenceCyclesRequireAnExplicitClonePolicy`
  (`SPF008`/`CFG011`: mutable clone shapes, constructor-bound cycles) →
  behavior covered by `Contracts/Fragments/*Clone*`
  (`DeepCloneContractTests`, `ConstructorBoundStructuralCycleCloneTests`,
  `CloneGraphTests`, `SharedMutableCollectionCloneTests`) and the
  `set-clones` consumer (build-only, §4).
- `UnsupportedStructuralChildRequiresExplicitReplace`
  (`SPF008`/`CFG011` + `SPF007`/`CFG010`) → structural-child behavior covered
  by `PrimaryConstructionTests` / `RequiredChildConstructionTests`.
- `UnsupportedConstructorBindingHasActionableDiagnostic` (`SPF003`/`CFG003`),
  `RequiredFieldHasActionableConstructionDiagnostic` (`SPF006`/`CFG004`),
  `BuiltInMergeValidationHasParity` (`SPF005`/`CFG005`),
  `StandaloneJsonConverterCollisionReportsSpf009` (`SPF009`),
  `ConfiglueFromJsonPatchCollisionReportsCfg013` (`CFG013`),
  root-model accessibility (`CFG002`) → no dedicated diagnostic-ID test
  remains; these are generator validation rules with no compiler/host row.
- `SharedFragmentOperationsHaveRuntimeParity`,
  `SharedPatchOperationsHaveRuntimeParity`,
  `PrivateRootConstructorAndSparseDefaultsConstructOnce` (incl. `required`
  variants), formatting/accessibility/root-model harnesses → runtime semantics
  stay covered on `net10.0` by `SparseFragments.Tests` and
  `Configlue.Tests` contracts (`RequiredConstructionTests`,
  `PrimaryConstructionTests`, patch/merge contracts).
