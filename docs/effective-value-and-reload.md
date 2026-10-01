# Effective-value changes and reloads

`IReadOnlyState<T>.OnChange` means an actual effective-value change. It is not a raw
revision or reload signal.

When a source reloads and the revision vector changes, Configlue diffs the newly resolved
effective model against the previous one. Listeners fire only when the resolved effective
value actually changes. A changed contribution that is shadowed by a higher-priority source
leaves the effective value unchanged, so `OnChange` does not fire.

Reactive adapters that are documented as effective-value streams follow `OnChange`, not raw
reloads.

## Reload-level signal

Reloads whose effective value did not change are exposed on a separate reload-level
diagnostics surface (`IConfiglueReloadDiagnostics.OnReload`). This is an advanced,
internal/diagnostics surface rather than part of `IReadOnlyState<T>`, because normal
consumers want effective values. Use it for diagnostics such as tracing that sources were
reloaded while the effective model stayed the same.

Reload failures remain a public diagnostics concern: subscribe to `OnReloadFailed` to
observe a failed reload, including failed source-local payload reads. A failed reload never
replaces the last successfully resolved effective value and does not surface as an
`OnChange` notification.

## Relationship to source fallback

Source-local malformed payloads are distinct from effective-model validation. A source that
reports `StateReadStatus.InvalidPayload` may participate in source fallback when the source
allows `StateFallbackCondition.InvalidPayload`. DataAnnotations and
`IConfiglueValidator<T>` failures are handled later by runtime read validation under
`ReadValidationMode` and never cause source fallback.
