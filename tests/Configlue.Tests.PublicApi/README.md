# Public API baselines

The tests compare each shipped assembly's public API with the checked-in files in `Approvals/`.

## API audiences

The namespace a type lives in records its intended audience:

- `Configlue` — application-facing API. Ordinary application code should live here (plus
  provider-specific fluent extension namespaces such as `Configlue.Provider.Json`).
- `Configlue.Extensibility` — provider and advanced composition SPI. Low-level registration ports
  such as `IConfiglueSourceRegistrationSink` and `ConfiglueSourceRegistration` live here and are
  implemented explicitly by root builders so they stay out of ordinary `builder.` / `sources.`
  completion.
- `Configlue.CompilerServices` — generated compiler/runtime ABI. These types remain CLR-public
  across assembly boundaries but are marked `EditorBrowsableState.Never` and are snapshotted
  separately.
- Low-level `Configlue.Sources` / `Configlue.Resources` / `Configlue.State` / `Configlue.Codecs` /
  `Configlue.Transformers` / `Configlue.Migrations` — provider and advanced composition SPI.
  These types are marked `EditorBrowsableState.Advanced` (or `Never` where generated code is the
  only caller) so ordinary IntelliSense and docs stay plumbing-free.

The full per-type audience table lives in `docs/public-api-audiences.md`. Every public type in
`Configlue.Abstraction` and `Configlue.Core` must have an entry there.

`ApiAudienceOwnershipTests` enforces the ownership model mechanically: exported namespaces stay
closed per assembly, and every `CompilerServices`, `Extensibility`, and low-level SPI type must
carry `EditorBrowsable` hiding. A pull request that adds a public type without hiding, or in a
new namespace, fails here by design.

When a public API change is intentional, regenerate the snapshots from the repository root with:

```powershell
$env:CONFIGLUE_UPDATE_PUBLIC_API = '1'
dotnet test --solution Configlue.slnx --configuration Release
```

Review the resulting approval-file changes before committing them.

`*.CompilerServices.approved.txt` snapshots review the generated ABI separately from application
and provider-facing types. CompilerServices types are excluded from the ordinary assembly snapshots.
