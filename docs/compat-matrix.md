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
| `RoslynSymbolCompat.IsRequired` | C# 9/older hosts + `required` members authored on modern SDKs | Kept. |
| `SparseJsonPatchEmitter` `#if NET5_0_OR_GREATER` guard around `UnconditionalSuppressMessage` | Modern-only trim/AOT attributes; `netstandard2.0`/`netstandard2.1`/net48 cannot resolve them | Kept. |
| `#if NETSTANDARD` / `#if NETSTANDARD2_0` branches in `src/` (hashing, file resources, host paths, SSM, Xml codec proxies) | `netstandard2.x` consumption assets incl. Unity 6 (`netstandard2.1`) and net48-via-`netstandard2.0` | Kept. None is net48-execution-only, so none was removed. |
| `tests/Configlue.Tests/Compatibility/ReadOnlySetSmokeShim.cs` (`IReadOnlySet<T>` for `NET48 || NETSTANDARD2_0`) | BCL gap: neither TFM ships `IReadOnlySet<T>`; the smoke consumer needs the identity so the generator recognizes set members | Kept, widened from net48-only now that the `netstandard2.0` smoke compiles the same portable set surface. Mirrors the `set-clones` / `package-sparse-netstandard` consumer shims. Never ships in a package. |

## 4. Test map

| Suite | Proves | Status |
| --- | --- | --- |
| `GeneratorHostCompatibilityTests` (Roslyn 4.3.1 host run, C# 9 emit, C# 9 standalone compile, MessagePack emit on old host, `IsExternalInit` shim mapping) | Unity 6 host contract | Kept minimal; exhaustive diagnostic/behavior matrices removed (not host-mapped). Runtime clone/merge/patch behavior stays covered in `SparseFragments.Tests` and `Configlue.Tests` contracts. |
| `Configlue.Tests` `netstandard2.0`/`netstandard2.1` Portable smoke (`IsTestProject=false`) | Compile-consumption of portable surface incl. generator execution | Kept. |
| `Configlue.Tests` `net48` Portable smoke | Same as above for the net48 consumer | Replaces full net48 suite re-execution (§5). |
| `package-sparse-netstandard` consumer (`netstandard2.0`/`net48` build, no built-in `IsExternalInit`) | Packed-generator consumption without BCL marker | Kept (CI `test-package-consumers.yaml`). |
| `set-clones` consumer (`netstandard2.0`/`net48`/`net10.0`) | Representative set-clone runtime semantics on legacy frameworks | Kept as manual consumer check (see its README). |
| `Configlue.Hosting.Unity.Tests` | Unity adapter behavior against engine test double | Kept. |

## 5. net48 policy

net48 is a compile-consumption target, not a runtime matrix target: the suite
executes on `net10.0`, while net48 builds the Portable smoke (the generator
still executes at compile time, so generation failures fail the build).
Removed with #281: the `net48` full-suite re-execution in
`test-strict.yaml`, the `net48` `Compile Remove` exclusion list and `Exe`
output in `Configlue.Tests.csproj`, the `#if NET48` AWS-SDK sync stubs, the
`#if NETFRAMEWORK` string-compare branch, and the `#if !NET48` AES/hosting
exclusions (dead once net48 no longer compiles those files).
