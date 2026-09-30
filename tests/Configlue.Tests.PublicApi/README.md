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

When a public API change is intentional, regenerate the snapshots from the repository root with:

```powershell
$env:CONFIGLUE_UPDATE_PUBLIC_API = '1'
dotnet test --solution Configlue.slnx --configuration Release
```

Review the resulting approval-file changes before committing them.

`*.CompilerServices.approved.txt` snapshots review the generated ABI separately from application
and provider-facing types. CompilerServices types are excluded from the ordinary assembly snapshots.
