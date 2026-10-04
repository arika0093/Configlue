# Set clone compatibility consumer

This consumer runs Configlue and SparseFragments generated models against
netstandard2.0 reference assemblies, .NET Framework 4.8, and .NET 10. It checks
HashSet comparer semantics, repeated cloning, and both property
orders for mutable/read-only aliases. SortedSet and other specialized
containers are intentionally unsupported (issue #280).

On legacy frameworks, the generated read-only set view also implements `ISet<T>`.
Because Configlue no longer exports a framework-named compatibility interface,
the netstandard2.0 and net48 consumer projects declare their own compatibility
`IReadOnlySet<T>` surface.
When the source is shared through `ISet<T>` and `IReadOnlySet<T>`, the two cloned
interface properties refer to the same object and mutations share the same data.
If a concrete HashSet property also references that source, its clone
and the read-only wrapper share a backing set; they need not be the same object.
The original set remains independent. Recognized BCL sets retain their comparers;
arbitrary custom set implementations use the default comparer.

From the repository root on Windows:

> Note: this consumer is build-only in CI (never executed there).
> The `net48` / `netstandard2.0` runtime executions below are manual-only
> ad-hoc verification on Windows and are unverified in CI
> (see `docs/compat-matrix.md` §5). Verified runtime stays on `net10.0`.

```powershell
dotnet build tests/fixtures/consumers/set-clones/SetClones.Consumer.csproj -c Release -p:TargetFramework= -p:CSharpier_Bypass=true
dotnet tests/fixtures/consumers/set-clones/bin/Release/net10.0/SetClones.Consumer.dll
& tests/fixtures/consumers/set-clones/bin/Release/net48/SetClones.Consumer.exe
& tests/fixtures/consumers/set-clones/bin/Release/net48/SetClones.Consumer.exe "$PWD/tests/fixtures/consumers/set-clones/bin/Release/netstandard2.0/SetClones.Consumer.dll"
```
