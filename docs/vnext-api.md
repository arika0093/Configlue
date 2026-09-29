Configlue vNext API migration
============================

The common workflow uses a generated model, registration, a state handle, and
asynchronous reads or generated sparse patches:

```csharp
using Configlue;
using Configlue.Provider.Json;

await using var context = ConfiglueApp.CreateContext(app =>
    app.Add<Settings>(model => model.UseJsonFile("settings.json")));

var state = context.GetState<Settings>();
var current = await state.GetValueAsync();
var receipt = await state.SaveAsync(new Settings.Patch
{
    Host = FragmentOperation<string>.Set("production"),
});

[ConfiglueModel("settings", Version = 1)]
public partial class Settings
{
    public string Host { get; set; } = "localhost";
}
```

Use `ConfiglueApp.Initialize`, `GetState<T>`, and `ShutdownAsync` for a
process-wide CLI or small application. Explicit contexts remain independent and
are preferable when an application needs scoped lifetimes, tests, DI, or several
configuration environments. Higher source priority wins; earlier registration
wins ties for each present member. Unset removes the selected contribution and
reveals the next source.

| Previous surface | vNext surface |
| --- | --- |
| `IConfiglueRuntime<T>` / `GetAdvancedOptions<T>` | `IReadOnlyState<T>` / `IWritableState<T>` for ordinary usage; focused capabilities below for advanced operations |
| State inspection through the aggregate interface | `GetInspection<T>()` / `IConfiglueInspection<T>.ReadAsync` |
| Draft sessions through the aggregate interface | `GetEditSessions<T>()` / `IConfiglueEditSessions<T>` |
| Topology and reload failure notifications | `GetDiagnostics<T>()` / `IConfiglueDiagnostics<T>` |
| Source-local writes and migrations | `GetSources<T>()` / `IConfiglueSources<T>` |
| Context-dependent `Sources` overloads / `SourcesForOptions` | `ConfigureSources(registration => ...)`, with `StateName`, `Services`, and `Sources` |
| Provider `Create` parameter lists | `Create<TFragment>(ConfiglueSourceCreationContext)` and `Complete(source)` |
| Core serialization/transformation helpers | Optional `Configlue.Extensibility` SDK, namespace `Configlue.Extensibility` |
| Direct read-result constructors / writable status or value | Validated `StateReadResult<T>` factories; default is `NotFound` |
| Nullable expected revision and absence flags | Explicit `RevisionCondition.None`, `Match(token)`, or `MustNotExist` |
| `StateMultiWriteResult` / application revision-only write results | `StateWriteReceipt`, with logical outcomes and physical write count |
| Generator runtime factory helpers and detail transport | Hidden `Configlue.CompilerServices` descriptor and member-path ABI |
| `RegisterAsSingleton` | Inject async state, or explicitly preload a fixed startup snapshot |

Generated details remain available through `state.GetDetailsAsync()`. The
snapshot transport and recursive path plumbing are compiler contracts; ordinary
applications consume generated details, including source and collection-element
provenance. Plans still accept typed selectors and `SourceKey<T>`; generated
member identity handles runtime resolution while property names remain readable
in diagnostics.

A Set is distinct from an Unset, including for null values. Source-local saves
apply sparse patches rather than rewriting resolved defaults into an overlay.
Edit sessions retain optimistic concurrency checks across observed sources and
resources. Several patches to one resource can share a physical write; failures
across resources retain partial-write diagnostics rather than implying a global
transaction.

The public surface has three audiences:

| Audience | Intended boundary |
| --- | --- |
| Application | Registration, async state, generated patches/details, sessions, diagnostics, sources, profiles, named/dynamic registries, validation, migrations |
| Provider author | Low-level state/resource/codec/source contracts in Abstraction; optional Extensibility helpers reference inward into Core |
| Compiler | Editor-hidden static model contracts, one facade descriptor, generated member paths and details transport in `Configlue.CompilerServices` |

Core does not reference the provider SDK or reactive libraries. Compiler ABI
types remain CLR-public because generated code runs in consumer assemblies;
dedicated public API approval files review them separately from application and
provider contracts. Runtime factories and builder internals remain internal.
Generated operations use closed model/fragment types and static dispatch for
Native AOT.

Callback notifications remain the primitive. Optional
`Configlue.Extensions.Reactive` and `Configlue.Extensions.R3` packages provide
native observables for value composition, selected-state distinctness, profile
switching, reload failures, and library-native scheduling. Initial read failures
terminate their value subscription; background reload failures are exception
values on a separate stream so later valid updates can continue.

Further contracts and examples:

- [Audience and dependency review](api-audiences.md)
- [Read/write outcomes and revision conditions](api-outcomes.md)
- [Generated member paths](generated-member-paths.md)
- [Synchronous boundaries](synchronous-boundaries.md)
- [Reactive integrations](reactive-integration.md)
- [Lifetime and source precedence](lifetime-and-precedence.md)
