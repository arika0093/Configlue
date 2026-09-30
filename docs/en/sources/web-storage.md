---
title: Browser WebStorage
description: Persist state in localStorage and sessionStorage from Blazor, with scoped lifetimes and explicit prerender semantics.
---

`Configlue.Extensions.Blazor` stores one serialized value in the browser's `localStorage` or `sessionStorage`. It works in Blazor Server through the circuit-scoped `IJSRuntime`, and in Blazor WebAssembly/Hybrid where the JavaScript runtime is application-lifetime.

## Register a storage source

The model-level helpers register the source and set it as the write destination:

```csharp
using Configlue.Extensions.Blazor;

services.AddConfiglue(builder =>
{
    builder.Add<UiPreferences>(model =>
    {
        model.UseLocalStorage("ui-preferences");
    });

    // Or the per-tab area:
    builder.Add<DraftState>(model =>
    {
        model.UseSessionStorage("draft");
    });
});
```

The lower-level API is available for composition and custom codecs:

```csharp
model.Sources(sources =>
{
    sources.FromLocalStorage("ui-preferences")
        .Named("ui")
        .Priority(10);
    sources.FromSessionStorage("ui-preferences-session");
});
```

`UseLocalStorage` and `UseSessionStorage` default to the JSON fragment codec. Pass a custom `Codec` through the options overload when the state uses another codec.

## Scoped runtime

A browser storage source consumes the scoped `IJSRuntime`, so it declares a scoped runtime requirement. Configlue creates and disposes that model's complete runtime per dependency-injection scope instead of sharing one runtime. In Blazor Server each circuit therefore gets its own runtime and its own `IJSRuntime`; browser-backed state cannot leak between circuits through a shared runtime.

This is independent of subject binding. Selecting a subject accessor with `PerSubject` scopes only the current-subject view; a model that uses a shareable source keeps its shared runtime even when it is per-subject. See [subject-scoped state](../advanced/subject-scoped-state.md).

## Blazor Server behavior

- **Prerender.** Before interactive rendering starts, JavaScript is unavailable. Reads report `Unavailable` and writes throw `WebStorageUnavailableException`; Configlue never substitutes a fake server-side store.
- **Disconnected circuit.** After a circuit disconnects, `IJSRuntime` throws. The same availability semantics apply, so callers can fall back or skip persistence instead of hanging.
- **Isolation.** The runtime and its storage resource are owned by the circuit scope, so state cannot leak between circuits or users.

## Subject-aware keys

For a per-subject model the storage key defaults to `{key}:{subjectKey}` so different subjects do not share one storage entry. Supply `KeySelector` when the application needs a different mapping.

## Writes and revisions

Values are stored in a small envelope carrying a revision alongside the payload. Conditional writes read the current revision through JavaScript and throw `StateConflictException` on mismatch. This is best-effort optimistic concurrency: browser storage is single-threaded per origin, but it is not a transactional backend.

## Security

Browser storage is client-controlled. Never treat it as authoritative authorization or security state, and never trust a value just because it came from `localStorage`. Configlue transformers can protect confidentiality or integrity when the key stays server-side, but client-side or WASM encryption does not establish trust when the key material is also available to the client.
