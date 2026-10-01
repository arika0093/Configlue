# Profiles as catalog-managed named states

A profile is a named state, not a separate namespace. The logical identity of any state -
fixed, dynamic, or profile - is `(TModel, StateName)`. Profiles, dynamic named states, and
fixed state names intentionally share one logical state-name namespace.

## Catalog is the source of truth

Profile membership and the active profile selection are persisted in a profile catalog.
The profile catalog is the source of truth for:

- which named states are profiles
- which profile is currently active

The runtime registry is a materialization/cache of named-state runtimes. It is not the
source of truth for profile existence. Registry lifetime and profile lifetime are therefore
decoupled:

- Removing or unloading a runtime from the dynamic-state registry must not delete the
  profile from the persisted catalog. A later `GetProfileAsync(name)` rematerializes the
  runtime.
- `CreateProfileAsync(name)` may adopt a dynamic named state that is already materialized,
  provided it does not conflict with a fixed or reserved logical state.
- `RemoveProfileAsync(name)` removes catalog membership and may unload the runtime, but
  leaves the backing configuration data intact so the same named state can be materialized
  again.

## Collision rule

A fixed state with the same `(TModel, StateName)` identity conflicts with profile creation.
The rule is expressed in terms of logical state identity, not registry implementation
details.

## No reserved user-visible names

Profile implementation does not reserve arbitrary user-visible state names. Names such as
`:`, `__`, `ActiveProfileName`, or `ProfileNames` are valid state names when they do not
collide with a fixed logical state. Internal profile-catalog storage uses its own internal
namespace/identity instead of borrowing user-visible state names. For example, the
single-binary catalog is stored at `models/{model}/profile-catalog/catalog.json`, separate
from the user-visible `profiles/` and `options/` entries.

## Public contract

`IConfiglueProfiledState<TModel>` describes profiles as persisted, catalog-managed named
states and documents the collision rule in terms of logical state identity.
