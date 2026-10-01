# Deterministic write ownership

Configlue resolves where an ordinary write goes without consulting read priority. A write
destination is the deterministic owner of the changed model paths, not the source that
happens to win a read.

`StateWriteRoute` has been removed. There is no API surface, diagnostic label, or legacy
code path that selects a write target by source priority or by trying successive candidates
until one reproduces the requested effective model.

## Write-ownership plan

Ownership is configured on the model with `Writes`:

```csharp
model.Writes(write =>
{
    write.DefaultTo(userSource);
    write.Route(x => x.Database, databaseSource);
});
```

- `DefaultTo(SourceKey<TModel>)` sets the owner for every changed path without a more
  specific route. A raw `string` overload remains as an advanced/dynamic surface.
- `Route<TValue>(Expression<Func<TModel, TValue>>, SourceKey<TModel>)` routes one property
  (and its descendants) to a source. A route for a nested model also applies to its
  descendants.

The immutable plan is `StateWritePlan`. The same plan drives patch saves, edit-session
commits, direct ordinary saves, mounted writes, and diagnostics. Registration-time routes
and per-operation routes are combined with `OverrideWith`: an operation route replaces the
registration route for the same path.

## Resolution order

Ordinary writes resolve their owner in this order:

1. A writable mounted source owns its mounted subtree.
2. An explicit property route in the write plan routes that path to the selected source.
3. Remaining root-level changes go to the configured default writable root source.
4. If exactly one non-explicit writable root source exists, it is inferred automatically.
5. If multiple non-explicit writable root sources exist and no default is configured,
   context creation fails as ambiguous.
6. Explicit-only sources are excluded from ordinary ownership inference and are writable
   only through explicit source operations.

Read priority is not part of this resolution. It only orders read resolution.

## Conflicts instead of rerouting

Once ownership selects a source, a write that cannot realize the requested edit because of
higher-priority or read-only contributions surfaces as a conflict. It never silently picks
another persistence target.

## Explicit-only sources

A source marked explicit-only is never inferred as an ordinary owner. It is written only
through explicit source operations. This keeps read-only or externally managed sources out
of the automatic write path.
