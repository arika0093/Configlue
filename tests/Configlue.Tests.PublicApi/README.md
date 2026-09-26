# Public API baselines

The tests compare each shipped assembly's public API with the checked-in files in `Approvals/`.
When a public API change is intentional, regenerate the snapshots from the repository root with:

```powershell
$env:CONFIGLUE_UPDATE_PUBLIC_API = '1'
dotnet test --solution Configlue.slnx --configuration Release
```

Review the resulting approval-file changes before committing them.
