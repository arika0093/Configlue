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
| `Configlue.DevTools.Web` | Loopback Blazor Web App host (Interactive Server) plus the single shared browser UI. | `net10.0` only |

Nothing web-related (and no Monaco dependency) lives in `Configlue.Core`,
`Configlue.Abstraction`, or ordinary hosting packages.

## Architecture

The launchable DevTools application is a real Blazor Web App:

```text
Application process
├─ Configlue runtime
└─ Configlue DevTools
   └─ Blazor Web App / Interactive Server (prerendering off)
        ├─ state/model selector
        ├─ BlazorMonaco effective-state editor
        └─ diagnostics/statistics tab
             ↓
          Browser
```

The Blazor components access the live Configlue runtime **in-process**
through the registered `ConfiglueDevToolsRegistry`. There is no
DevTools-specific REST transport: the normal local development experience
needs no `/api/*` endpoints, no SSE feed, and no extra WebSocket protocol
beyond the Interactive Server circuit itself.

- `ConfiglueDevToolsWebHost` builds and owns a loopback-only
  `WebApplication`: `AddRazorComponents()` plus
  `AddInteractiveServerComponents()`, with
  `MapRazorComponents<DevToolsApp>()` in `InteractiveServerRenderMode`.
  Prerendering stays off; UI renders after the circuit connects.
- The root document (`DevToolsApp`) renders statically: title, the Blazor
  boot scripts, the embedded Monaco bridge script, and the page-memory
  session-token bootstrap that authorizes circuit requests.
- `ConfiglueDevToolsHome` (`/`) hosts `ConfiglueDevToolsShell`, which
  renders the state/model selector, the `Editor | Diagnostics` tabs, a
  development-tooling banner, and the live editor or diagnostics panel for
  the current selection.

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
to the DevTools package; the non-generic editor dispatcher is a DevTools-only
renderer inside the web package.

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

- Loopback only: the server binds `127.0.0.1`. There is no option for public
  binding.
- Port `0` (default) asks the OS for a free port; the actual `Url` and the
  token-bearing `LaunchUrl` are exposed for browser launch.
- Every application request (including `/`) requires the per-host random
  session token via `X-Configlue-DevTools-Token`, `Authorization: Bearer`, or
  the `token` query parameter. A missing or wrong token yields `403`.
- The token gate also protects the Blazor circuit negotiation
  (`/_blazor/negotiate` and the `/_blazor` upgrade): the in-page bootstrap
  carries the token in page memory and attaches it to circuit requests, so a
  circuit cannot be negotiated without it.
- The only ungated paths are the shared framework boot assets under
  `/_framework/` (embedded in this package, identical for every application,
  carrying no state), which plain script tags and module imports must load
  without custom headers.
- Shutdown is deterministic; stopping the host releases the port.

## Security

- Loopback only; non-loopback remotes are refused even if they reach the socket.
- Random 32-byte session token per host instance unless the application
  supplies one.
- `#244` redaction is enforced in the projection layer: secret plaintext never
  appears in serialized payloads (state JSON, schema is metadata-only,
  diagnostics, check results, error details) and never enters the Monaco model
  or the initial document.
- The browser page keeps the token in page memory only. It never writes
  credentials or tokens to `localStorage`, `sessionStorage`, or cookies, and
  never exposes raw authorization headers.
- Diagnostics failures surface the failure type only, never raw messages that
  could carry secret values.
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

## Effective-state editor

The main editor is the BlazorMonaco semantic editor
(`ConfiglueEffectiveStateEditor<TModel>`, `#248`), composed per selection by
the non-generic `ConfiglueDevToolsEditorHost` dispatcher:

- Monaco JSON editor over the effective-state projection;
- semantic `EditSession` draft with Save / Discard;
- Validate, Diff, and Rebase;
- an explicit secret-change flow that lives outside the Monaco model (empty
  means unchanged; the transient input is cleared after apply or cancel);
- upstream-change indication: clean sessions follow upstream automatically,
  dirty sessions are never silently overwritten.

Changing the selection recreates the editor subtree: the previous session is
disposed and the next state opens a fresh session, so a draft from one
state/subject can never commit to another state.

`ConfiglueEffectiveStateViewer<TModel>` (`#247`) remains the read-only
provenance overlay building block (deterministic canonical JSON,
member-path to Monaco-range mapping from generated schema metadata,
source/inlay labels, hover/explain, secret/read-only/invalid markers,
browser-side JSON language-service setup from the generated schema, runtime
validation markers). The editor reuses its projection and overlay pipeline.

Architecture notes:

- BlazorMonaco owns editor lifecycle, values, minimal text edits, and
  decorations. The narrow `configlue-devtools-monaco.js` bridge
  (`ConfiglueMonacoBridge`) covers only Monaco APIs BlazorMonaco does not
  wrap cleanly (JSON language-service setup, hover content, inlay labels,
  server-side markers). No runtime object graph is mirrored to the browser.
- Synchronization reuses the existing Interactive Server circuit: initial JSON
  on state load, minimal Monaco edits on watched changes (scroll/selection
  preserved), decoration-only deltas without document payloads. No new
  SSE/WebSocket protocol and no hidden extra backend reads (one consistent
  snapshot per load/refresh; contribution projections reuse the cached
  snapshot).

## UI scope

The browser surface is the Blazor app described above:

- a state/model selector covering registered, named, and dynamic states;
- the BlazorMonaco effective-state editor with Save/Discard, Validate, Diff,
  Rebase, the secret flow, and upstream indication;
- a diagnostics/statistics tab (secondary) with an explicit check runner;
- a development-tooling banner.

There is intentionally no form framework, theme/plugin system, per-type editor
framework, production operator console, or source administration UI.

### Diagnostics / statistics tab (secondary)

The tab summarizes existing runtime data only; it introduces no new
inspection model or instrumentation:

- State: model id/version, state name, active subject where safe
  (opaque key, `default` for server-wide), last resolution/reload/write,
  and validation status from cached events.
- Sources: per-source kind/display name, priority/order, read/write/watch
  capabilities, cached read status, watcher state, revision presence
  (never values), last error category, plus an explicit **Run check**
  action. Active checks never run on tab open.
- Value/provenance statistics from the current details snapshot, loaded on
  explicit request: leaf counts (a leaf is one scalar member or one whole
  collection; nested objects expand and are never counted), per-source
  effective ownership, editable vs read-only vs shadowed vs no-target, secret
  counts without values, shadowed contributions (model defaults excluded),
  and missing/unavailable/invalid tallies.
- Recent activity: a bounded table (last 50) from `#65` events when
  enabled; empty when `EventHistoryCapacity` is zero. No polling;
  refresh via the Refresh button, which re-reads cached snapshots.

Secret values and sensitive metadata stay redacted per `#244`;
the tab counts secrets but never reveals values.

## Compatibility

| Consumer | Can use |
| --- | --- |
| `net10.0` applications and hosts | Full host (`Configlue.DevTools.Web`) plus projections |
| `netstandard2.0` / `netstandard2.1` libraries | Projections only (`Configlue.DevTools`); host the Blazor UI from a `net10.0` process |
| Unity / Godot / MAUI / Avalonia / Blazor | Bind their already-running state into the shared browser UI; no per-host native inspector |

## Tests

Host-level tests cover explicit opt-in, loopback defaults, token
authorization (document, headers/bearer, circuit negotiation), Interactive
Server service registration with the live registry, startup/shutdown/
disposal, the session-token-gated bridge script, named and dynamic registry
states, selection-scoped live editors with dispose/recreate semantics, the
`#244` redaction boundary, no new inspection API, and the absence of the
legacy REST transport.
Projection, viewer, editor-session, and component tests cover deterministic
projection, nesting/collections/nulls/naming, range mapping, source
annotation, shadowed hover, read-only markers, schema setup, runtime markers,
redaction, watch updates, no source-syntax leak, decoration-only updates,
draft throttle/debounce, semantic save/discard/validate/diff/rebase, the
secret flow, upstream handling, and explicit-check-only diagnostics.
Fakes only; no real browser is required. (Browser-level Monaco behavior is
tracked in the Monaco follow-up issue.)

## Browser-launch hooks (`#250`)

Thin, optional helpers open the **same** shared browser UI from each host.
The pattern is always:

```text
host action/menu/debug hook -> ensure/get DevTools session URL -> open system browser
```

No WebView2/BlazorWebView/embedded controls, no BlazorMonaco assets in hosting
packages, and no native inspector duplication. Launch helpers never start
servers, so repeated opens reuse the same URL and never start duplicates.
Referencing a hosting package never enables DevTools: `ConfiglueDevTools`
starts disabled and an explicit loopback launch URL must be published.

The DevTools host runs on its own loopback endpoint (never mapped into a
public application host). Every application request still requires the
per-host session token; launch URLs carry the token verbatim and are never
logged.

### Plain .NET / ASP.NET Core / Blazor

```csharp
#if DEBUG
var registry = new ConfiglueDevToolsRegistry();
registry.Add(context.GetState<AppSettings>());
await using var devtools = ConfiglueDevToolsWebHost.Create(registry);
await devtools.StartAsync();
ConfiglueDevTools.Enable(devtools.LaunchUrl);
await AspNetCoreConfiglueDevTools.OpenBrowserAsync();
// Blazor apps use BlazorConfiglueDevTools the same way; there is no second UI.
#endif
```

### WPF / WinForms / WinUI / Avalonia / MAUI

```csharp
#if DEBUG
ConfiglueDevTools.Enable(devtools.LaunchUrl);
await WpfConfiglueDevTools.OpenBrowserAsync(); // WinForms/WinUI/Avalonia/MAUI equivalents
#endif
```

### Unity (Editor only)

```csharp
// Runtime/play mode:
ConfiglueDevTools.Enable(launchUrlFromEditorHost);
await UnityConfiglueDevTools.OpenBrowserAsync();
```

Editor menu `Tools > Configlue > Open DevTools` opens the active session.
Menu code is `#if UNITY_EDITOR` only and never ships in player builds.

### Godot (editor only)

```csharp
ConfiglueDevTools.Enable(launchUrlFromDevHost);
await GodotConfiglueDevTools.OpenBrowserAsync(); // uses OS.ShellOpen
```

The `ConfiglueDevToolsEditorPlugin` tool menu (`#if TOOLS` only) opens the
active session and is excluded from exported games.
