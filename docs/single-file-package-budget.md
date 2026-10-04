# Single-file settings: package and dependency budget (#231)

This note audits what installing the convenience `Configlue` package brings into a
basic JSON settings application, and records why the graph is kept as one package.

Measured with `dotnet list src/basic/Configlue/Configlue.csproj package --include-transitive`
on the #231 worktree.

## Dependency graph

`Configlue` (convenience) references these projects, which ship as its dependencies:

- `Configlue.Core` — DI-free resolution runtime.
- `Configlue.Abstraction` — backend-neutral contracts.
- `Configlue.Extensions.DI` — Generic Host integration.
- `Configlue.Provider.Json` — JSON file sources and codecs.
- `Configlue.Source.Http` — read-only JSON-over-HTTP policy sources.
- `Configlue.Source.Environment` — environment-variable sources.
- `Configlue.Generator` — source generator (analyzer asset only, no runtime cost).

Transitive runtime packages for `net10.0`:

- `Microsoft.Extensions.Configuration(.Abstractions/.Binder)`,
  `Microsoft.Extensions.DependencyInjection(.Abstractions)`,
  `Microsoft.Extensions.Diagnostics(.Abstractions)`,
  `Microsoft.Extensions.Http`,
  `Microsoft.Extensions.Logging(.Abstractions)`,
  `Microsoft.Extensions.Options(.ConfigurationExtensions)`,
  `Microsoft.Extensions.Primitives`
  (all 10.0.12, pulled in by the HTTP policy and environment sources).
- `System.IO.Hashing` 10.0.0 (file revisions, backups, locks).

Transitive runtime packages for `netstandard2.0` additionally include
`System.Text.Json` 10.0.0, `System.Memory`, `System.Threading.Channels`,
`Microsoft.Bcl.TimeProvider`, `Microsoft.Bcl.AsyncInterfaces`,
`System.Threading.Tasks.Extensions`, and `System.ComponentModel.Annotations`
(`net10.0` gets all of these from the shared framework instead).

## Startup and steady-state cost

- Referencing the package does not create a DI container, an `HttpClient`, or any
  watcher. Those objects are built only when the application registers the
  corresponding source.
- A single-file context (`Add<T>().UseLocalJson(path)`) constructs one file resource,
  one codec, one serialized source, and one runtime. No HTTP, environment, or options
  machinery is instantiated.
- The runtime detects this topology at construction and takes the single-source fast
  path: the write target is pre-resolved once, reads skip multi-source resolver scans
  and dictionary partitioning, revision/provenance structures stay unallocated for the
  single entry, and watching awaits the one file watcher directly.

## Trimming and NativeAOT

- Configlue libraries avoid unguarded reflection on configuration paths. Member
  validation reflects only when dynamic code is supported and is annotated for the
  trimmer; unused sources (HTTP, environment, DI) contain no rooted singletons, so a
  trimmed publish drops them when the application never registers them.
- For NativeAOT JSON, register a source-generated `JsonSerializerContext`
  (for example `[JsonSerializable(typeof(MySettings))]`) and set it as
  `JsonSerializerOptions.TypeInfoResolver`, including metadata for member types not
  reached from the model. The generator emits the model operations; no runtime
  code generation is required.

## Decision

The HTTP, environment, and DI dependencies are retained in the default package for
discoverability: the product property is that a tiny-file application grows into
layered configuration (local file plus environment overrides plus remote policy)
without changing consumer code or migrating libraries. Splitting the convenience
package was evaluated and rejected for now — it would fragment exactly the growth path
the single-file scenario promises, without a measured startup or size benefit
(the extra assemblies are inert until used and trimmable when unused).
Revisit only with published size/startup numbers showing otherwise.
