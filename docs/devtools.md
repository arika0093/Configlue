# Configlue DevTools (development-only browser UI)

> A lightweight development/debugging UI for inspecting and making small
> controlled edits to Configlue state.

This is **not** a production settings framework and not a core Configlue
feature. The packages are optional and stay out of normal production
dependency graphs. This direction supersedes the host-specific inspector
approach once explored for WPF/WinForms/WinUI/MAUI/Unity/Godot/Avalonia:
there is a single shared browser UI instead of one inspector per host.

## Packages

| Package | Contents | TFMs |
| --- | --- | --- |
| `Configlue.DevTools` | Host-neutral projections over live state (JSON canonical form, diagnostics, check, edit-session writes). No HTTP, no browser assets. | `netstandard2.0`, `netstandard2.1`, `net10.0` |
| `Configlue.DevTools.Web` | Loopback web host plus the single shared browser page. | `net10.0` only |

Nothing web-related (and no Monaco dependency) lives in `Configlue.Core`,
`Configlue.Abstraction`, or ordinary hosting packages.

## Runtime connection model

The host binds to the **already constructed** live in-process runtime. It never
rediscovers or re-executes application bootstrap code. It consumes only
existing stable surfaces:

- effective-value reads (the same resolution behind generated `GetDetailsAsync()`);
- the details-snapshot transport behind `GetDetailsAsync()` for source/editability metadata;
- diagnostics snapshots and events (`GetDiagnostics()`, runtime snapshots, recent events);
- operational checks via diagnostics `Check()` (no separate inspection API);
- edit sessions for controlled writes;
- generated model/schema metadata (`ConfiglueModelSchema`, secret flags);
- state registries and named states.

No new public `InspectAsync()` API is introduced. UI projections are internal
to the DevTools package.

## Explicit opt-in

DevTools does nothing until the application explicitly binds live state and
starts the host. There is no background work when disabled.

Non-DI usage:

```csharp
var registry = new ConfiglueDevToolsRegistry();
registry.Add(context.GetState<AppSettings>());
#if DEBUG
await using var devtools = ConfiglueDevToolsWebHost.Create(registry);
await devtools.StartAsync();
// Open devtools.LaunchUrl in a browser.
#endif
```

Dependency-injection usage:

```csharp
#if DEBUG
services.AddConfiglueDevToolsWeb(registry);
#endif
```

The registry must already reference live runtime instances. The host starts
only when the application resolves it and calls `StartAsync`, and it is
disposed deterministically (`StopAsync` / `Dispose` / `DisposeAsync`, or with
the DI container). Per-host browser auto-open is a separate follow-up; the
host remains fully usable with auto-open disabled (`AutoOpenBrowser` is
recorded but this package never launches a browser process).

## Local host behavior

- Loopback only: the listener binds `127.0.0.1`. There is no option for public
  binding.
- Port `0` (default) asks the OS for a free port; the actual `Url` and the
  token-bearing `LaunchUrl` are exposed for browser launch.
- Every request (including `/`) requires the per-host random session token via
  `X-Configlue-DevTools-Token`, `Authorization: Bearer`, or the `token` query
  parameter. A missing or wrong token yields `403`.
- Shutdown is deterministic; stopping the host releases the port.

## Security

- Loopback only; non-loopback remotes are refused even if they reach the socket.
- Random 32-byte session token per host instance unless the application
  supplies one.
- `#244` redaction is enforced in the projection layer: secret plaintext never
  appears in serialized payloads (state JSON, schema is metadata-only,
  diagnostics, check results, error details).
- The browser page keeps the token in page memory only. It never writes
  credentials or tokens to `localStorage`, `sessionStorage`, or cookies, and
  never exposes raw authorization headers.
- Never enable DevTools in production. Non-loopback binding, if ever
  supported, requires explicit configuration plus an application-supplied
  authorization policy; that is documented here and not implemented.

## Canonical representation

JSON is the canonical DevTools representation regardless of the backing
Source (YAML, environment, Vault/Key Vault, PostgreSQL, Redis, S3 all project
to JSON). Secret subtrees are replaced with the `********` placeholder.
Edits are semantic model edits routed through normal edit-session writes, not
a raw source-file editor. Saving a payload that still contains the redacted
placeholder for a secret preserves the existing secret instead of writing the
placeholder.

## Effective-state viewer (`#247`)

`ConfiglueEffectiveStateViewer<TModel>` renders the resolved state as
deterministic canonical JSON (generated model/schema order,
naming-policy-aware wire names, nested models and collections, deterministic
nulls, no source syntax or comments) and overlays provenance without touching
the text:

- effective source (compact label/inlay), read-only lock/muted state, secret
  markers, and invalid markers via Monaco decorations, glyph margin, inlay
  hints, and hover;
- hover explains effective and shadowed contributions (source, editable
  state, locator such as the env key); secrets show safe state only, never
  plaintext;
- Monaco's browser-side JSON language service is configured once per
  model/schema from the generated schema (syntax, validation, enum
  completion, descriptions, constraints); Configlue runtime validation beyond
  the schema arrives as extra markers.

Architecture notes:

- BlazorMonaco owns editor lifecycle, values, minimal text edits, and
  decorations. The narrow `configlue-devtools-monaco.js` bridge
  (`ConfiglueMonacoBridge`) covers only Monaco APIs BlazorMonaco does not
  wrap cleanly (JSON language-service setup, hover content, inlay labels,
  server-side markers). No runtime object graph is mirrored to the browser.
- Member-path to Monaco-range mapping is emitted alongside the projection
  from generated schema metadata (AOT-safe, no reflection); decorations reuse
  it instead of rescanning the document.
- Read-only in this issue; semantic editing is `#248`. The viewer is
  read-only (`ReadOnly = true`); no draft or save path is introduced here.
- Synchronization reuses the existing Interactive Server circuit: initial JSON
  on state load, minimal Monaco edits on watched changes (scroll/selection
  preserved), decoration-only deltas without document payloads. No new
  SSE/WebSocket protocol and no hidden extra backend reads (one consistent
  snapshot per load/refresh; contribution projections reuse the cached
  snapshot).
- The loopback host additionally serves `/api/viewer` (with `knownVersion`
  delta omission), `/api/viewer-schema`, and `/api/viewer-contribution`
  (normalized per-member contribution JSON, redacted; no raw source docs).

## UI scope

The browser surface has two layers:

- The loopback host page: a state/model selector, a plain `<textarea>`
  fallback JSON view, an optional diagnostics/statistics view, schema view,
  check runner, save/discard, and a development-tooling banner.
- The BlazorMonaco effective-state JSON viewer
  (`ConfiglueEffectiveStateViewer<TModel>` in `Configlue.DevTools.Web`,
  `#247`): the canonical DevTools representation for Blazor Server UI.
  Monaco runs browser-side while the Configlue runtime stays server-side;
  only compact state/details metadata crosses the circuit.

There is intentionally no form framework, theme/plugin system, per-type editor
framework, production operator console, or source administration UI.

## Compatibility

| Consumer | Can use |
| --- | --- |
| `net10.0` applications and hosts | Full host (`Configlue.DevTools.Web`) plus projections |
| `netstandard2.0` / `netstandard2.1` libraries | Projections only (`Configlue.DevTools`); host a web endpoint from a `net10.0` process |
| Unity / Godot / MAUI / Avalonia / Blazor | Bind their already-running state into the shared browser UI; no per-host native inspector |

## Tests

HTTP-level assertions against the loopback server cover opt-in, loopback
defaults, startup/shutdown/disposal, discovery/selection, named and dynamic
registry states, disabled-means-no-server, the `#244` redaction boundary, no
new inspection API, and no mutation without an explicit edit-session save.
Viewer tests cover component lifecycle, deterministic projection,
nesting/collections/nulls/naming, range mapping, source annotation, shadowed
hover, read-only markers, schema setup, runtime markers, redaction, watch
updates, no source-syntax leak, and no full-document traffic for
decoration-only updates.
Fakes only; no real browser is required.
