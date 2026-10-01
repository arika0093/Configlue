# Headless Blazor settings page

`Configlue.Hosting.Blazor` provides two headless components that adapt the Core
snapshot and edit-session APIs to Blazor. They render no visual controls and do not
wrap `EditForm`; the application supplies its own markup and form components.

Register state with dependency injection as usual (per-subject registration works the
same way):

```csharp
builder.Services.AddConfiglueState<AppSettings, AppSettings.Fragment>(
    new StateSourceSet<AppSettings.Fragment>([
        new("user", userStore, writer: userStore, watcher: userStore),
    ])
);
```

## Read-only view

`StateReader<T>` resolves one snapshot (value plus provenance/editability) and keeps
it fresh. Reload failures never replace the last successfully rendered value.

```razor
<StateReader T="AppSettings" Context="state">
    @{
        var details = state.Snapshot!.GetDetails();
    }

    @if (state.IsLoading)
    {
        <p>Loading…</p>
    }
    else
    {
        <dl>
            <dt>Name</dt>
            <dd>@state.Value.Label (@details.Label.Source?.DisplayName)</dd>
            <dt>Retry count</dt>
            <dd>@details.RetryCount.Value (@details.RetryCount.Source?.DisplayName)</dd>
        </dl>

        @if (state.ReloadFailure is not null)
        {
            <p role="alert">Reload failed: @state.ReloadFailure.Message</p>
        }
    }
</StateReader>
```

`state.Snapshot.GetDetails()` returns the generated details tree for the *same*
resolution as `state.Value`, so editability and provenance cannot disagree with the
displayed value.

## Editable form

`StateEditor<T>` owns a Core `EditSession<T>` and exposes an `EditContext` for standard
Blazor fields. Use generated `Details.IsEditable` to disable fields that are shadowed,
read-only, or have no write target.

```razor
<StateEditor T="AppSettings" Context="state" OnError="OnEditorError">
    @{
        var details = state.SessionStart.GetDetails();
    }

    <EditForm EditContext="state.EditContext" OnValidSubmit="state.SaveAsync">
        <DataAnnotationsValidator />

        <label>
            Name
            <InputText @bind-Value="state.Value.Label"
                       disabled="@(!details.Label.IsEditable)" />
        </label>
        <small>@details.Label.Source?.DisplayName</small>

        <label>
            Retry count
            <InputNumber @bind-Value="state.Value.RetryCount"
                         disabled="@(!details.RetryCount.IsEditable)" />
        </label>

        @if (state.HasUpstreamChanges)
        {
            <p role="status">
                Settings changed elsewhere.
                <button type="button" @onclick="() => state.RebaseAsync()">Rebase</button>
                <button type="button" @onclick="state.ResetToUpstream">Discard mine</button>
            </p>
        }

        @if (state.IsSubjectChanged)
        {
            <p role="alert">The signed-in subject changed while this form was dirty.</p>
        }

        <button type="submit" disabled="@state.IsSaving">Save</button>
        <button type="button" @onclick="state.ResetToSessionStart">Revert</button>
    </EditForm>
</StateEditor>
```

### Upstream changes

By default a **clean** session automatically rebases when upstream changes and the form
refreshes. A **dirty** session is never overwritten: the draft is preserved and
`HasUpstreamChanges` becomes `true` so the user can rebase, reset, or keep editing.
Set `AutoRebaseOnCleanUpstreamChange="false"` to handle clean changes manually too.

### Subject changes

A Core edit session is pinned to the subject that was current when it opened. On a
subject/authentication change a clean editor transparently reopens against the new
subject, while a dirty editor preserves its draft and sets `IsSubjectChanged`. Saving a
dirty draft after a subject change is blocked by default (so an old subject's draft is
not written accidentally); opt in with `AllowSavingInvalidatedDraft="true"` if that is
intended. Use `OnSubjectChanged` to warn, discard, or navigate.

### Validation, conflicts, and partial writes

Configlue validation, write conflicts, and partial multi-source writes are surfaced
through `OnError` as categorized `StateEditorErrorEventArgs<T>`, retaining the
underlying Core exception (`ValidationException`, `ConflictException`,
`MultiWriteException`). Blazor validation continues to flow through the standard
`EditContext`, so `ValidationMessage`/`DataAnnotationsValidator` work unchanged.

```csharp
void OnEditorError(StateEditorErrorEventArgs<AppSettings> e)
{
    switch (e.Kind)
    {
        case StateEditorErrorKind.Validation:
            // e.ValidationException!.Failures
            break;
        case StateEditorErrorKind.Conflict:
            // e.ConflictException
            break;
        case StateEditorErrorKind.MultiWrite:
            // e.MultiWriteException!.Completed / FailedSourceIds
            break;
    }
}
```
