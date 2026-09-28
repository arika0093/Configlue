Generated member paths
======================

Application code continues to configure write routes with typed selectors and
typed source keys:

```csharp
var plan = StateWritePlan.For<AppSettings>()
    .Route(x => x.Database, SourceKey<AppSettings>.Named("database"))
    .Route(x => x.Database!.Host, SourceKey<AppSettings>.Named("host"))
    .Build();
```

The most specific route wins. An operation plan replaces registration routes
for the same selected property. A route for a nested model covers its
descendants, and replacing that model with null is rejected when a more specific
child route makes the operation ambiguous.

Selectors and compatibility string routes are validated against generated schema
metadata when the plan is built or bound to an options runtime. Normal patch and
edit-session routing compares immutable member-ID sequences. Generated details
use the same paths for editability, sparse source contributions, and collection
provenance. Property names are formatted for diagnostics rather than parsed in
those lookups.

IDs are local to their generated schema. Paths therefore include the root model
type, schema ID, and version, as well as the complete sequence of nested member
IDs. Two properties containing the same nested model remain different paths.
Combining plans or resolving a path from a different root model is rejected.

`ConfiglueMemberPath` and `ConfiglueWriteRouting` belong to the hidden
`Configlue.CompilerServices` boundary. Ordinary applications use selectors,
generated patches, and generated details. Dotted `PropertyRoutes` remain
available for diagnostics and compatibility; their strings do not determine
normal runtime path identity.

Source identity remains an application-defined logical name at runtime because
sources can be named, projected, mounted, or dynamically registered. Public
`SourceKey<TModel>` selectors preserve model typing, and runtime registration
checks that their targets exist and are writable. Source names are not replaced
with generated member IDs.

`WriteRouteBenchmarks` compares compatibility string lookup with lookup through a
precompiled generated path and measures route-below checks. Run it with:

```shell
dotnet run --project benchmarks/Configlue.Benchmarks -c Release -- --filter '*WriteRouteBenchmarks*' --job Short
```
