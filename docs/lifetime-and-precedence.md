# Lifetime and source precedence

Configlue exposes two first-class lifetime models, and both are supported parts of
the public surface.

## Process-wide convenience

`ConfiglueApp.Initialize(...)` configures one process-wide default context for CLI
tools and small applications where explicit context propagation adds ceremony.
`ConfiglueApp.GetOptions<T>()` (and the inspection, edit-session, diagnostics, and
sources accessors) resolve handles from that context. `await
ConfiglueApp.ShutdownAsync()` disposes the context and clears it.

The lifecycle is deliberately strict and test friendly:

- Accessing `GetOptions<T>()` before `Initialize` throws `InvalidOperationException`.
- Initializing while a default context is already active throws
  `InvalidOperationException`; the newly built context is disposed rather than leaked.
- `ShutdownAsync` is idempotent. Calling it with no active context completes without
  side effects.
- After `ShutdownAsync`, `Initialize` can be called again to build a fresh default
  context. This is the supported shape for CLI test harnesses that run several
  process-wide scenarios in one process.

`Initialize` is synchronous configuration only: it builds the context but does not
block on source I/O. Reads and writes stay asynchronous through the resolved handles.

## Explicit contexts

`ConfiglueApp.CreateContext(...)` returns an independent `ConfiglueContext` that is
`IDisposable` and `IAsyncDisposable`. Prefer explicit contexts for DI, tests,
libraries, multiple simultaneous environments, and scoped or composable lifetimes.
Contexts are isolated from one another and from the process-wide default.

A context owns the options and watcher tasks it creates, plus resources created by
provider registration helpers (for example `FromJsonFile`). Source, reader, writer,
and resource instances supplied by the application remain caller-owned and must be
disposed by the caller. For DI, the service provider owns the context.

## Source precedence

Both lifetime models resolve values from a `StateSourceSet<T>`. Precedence is a
deterministic contract:

- Sources are ordered by descending numeric `Priority`. A higher number wins.
- Sources with equal priority keep registration order; the earlier registered source
  wins the tie. `StateSourceSet<T>.Sources` exposes this exact order, so resolver
  order, provenance, and equal-priority shadowing all agree.
- Resolution considers members, not whole sources. Missing or fallback-conditioned
  members continue to the next source in priority order.
- An `Unset` removes the selected contribution and reveals the next lower-priority
  source, rather than rewriting it into an overlay.
- Write routes are chosen independently of read priority, so an edit can target a
  lower-priority overlay while a higher-priority source keeps shadowing the value.

See [Resolution and merge](en/layering/resolution-and-merge.md) for presence and
merge modes, and [Read and write outcomes](api-outcomes.md) for revision conditions
and receipts.

## Related guidance

- [Application setup](en/basic-usage/app-setup.md) documents the same two lifetime
  models for application authors.
- [Synchronous boundaries](synchronous-boundaries.md) explains why the process-wide
  entry point stays synchronous while reads and writes remain asynchronous.
