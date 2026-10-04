# Single-file settings: package and dependency budget (#231, redefined by #261)

This note audits what installing the primary `Configlue` package brings into a
basic JSON settings application.

Measured with `dotnet list src/basic/Configlue/Configlue.csproj package --include-transitive`
after #261.

## Dependency graph

`Configlue` (primary/default) references these projects, which ship as its dependencies:

- `Configlue.Core` — storage/format/host-neutral resolution runtime and composition machinery.
- `Configlue.Abstraction` — backend-neutral contracts.
- `Configlue.Provider.Json` — JSON codecs (file composition lives in the standard layer).
- `Configlue.Source.Environment` — environment-variable sources.
- `Configlue.Generator` — source generator (analyzer asset only, no runtime cost).

File (`FileResource`), ZIP (`ZipEntryResource` used by `SingleBinary`), standard
paths, `UseLocalJson`/single-file settings, `UseCommonSources` presets, and
`UseSingleBinary` are built into the standard `Configlue` assembly itself.

Explicitly NOT in the default graph (opt-in packages):

- `Configlue.Extensions.DI` — Microsoft DI integration.
- `Configlue.Source.Http` — remote JSON-over-HTTP policy sources (including `WithHttpPolicy`).
- `Configlue.Source.CommandLine` — depends on `System.CommandLine`.
- `Configlue.Transformer.AES` — narrows TFMs (`netstandard2.1`+).
- YAML / MessagePack / XML and other external-format providers (`SharpYaml`, `MessagePack`).
- Database, cloud, hosting, compression, and other specialized packages.

Transitive runtime packages for `net10.0`:

- `Microsoft.Extensions.Logging.Abstractions` 10.0.0 (diagnostic logging contracts;
  it brings `Microsoft.Extensions.DependencyInjection.Abstractions` as its own
  contract dependency, not the `Configlue.Extensions.DI` integration),
  `System.Diagnostics.DiagnosticSource` 10.0.5, `System.IO.Hashing` 10.0.0
  (file revisions, backups, locks), `System.IO.Pipelines` 10.0.0.
- No `Configlue.Extensions.DI`, `Configlue.Source.Http`,
  `Microsoft.Extensions.Http`, `System.CommandLine`, `SharpYaml`, `MessagePack`,
  hosting, or specialized packages.

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

#261 redefines the default around the dependency-free experience: local JSON,
file watching/backups, environment overrides, common sources, and SingleBinary
stay in `Configlue`; HTTP, DI, CommandLine, external formats, and specialized
backends are explicit opt-ins. The product property is that a tiny-file
application grows into layered file + environment configuration without changing
consumer code or migrating libraries, while remote/specialized integrations are
added only when actually used.
