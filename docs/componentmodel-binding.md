# ComponentModel binding for desktop and native UI

`Configlue.Extensions.ComponentModel` is a host-neutral binding layer on top of the Core
snapshot and edit-session contracts. It lets WPF, WinForms, Avalonia, .NET MAUI,
WinUI/Windows App SDK, and similar .NET UI stacks share the same read, edit, validation,
and upstream-change behavior without Core depending on any UI framework.

It is a library integration, so it stays in the `Extensions.*` family. Framework- or
host-specific pieces (dispatchers, storage, subject resolution) belong in
`Configlue.Hosting.*` or in small adapters in the consuming application.

## Generated observable proxies

The source generator emits a nested `Observable` type for every generated model. The
adapter wraps the current model in that proxy, so user models never have to implement
`INotifyPropertyChanged` themselves.

```csharp
var proxy = (AppSettings.Observable)editor.Value!;

proxy.Label = "edited";            // raises PropertyChanged("Label")
proxy.Database!.Host = "db.local"; // nested edit is observed and marks the draft dirty
```

Semantics:

- Scalar setters mutate the underlying model, raise `PropertyChanged`, and notify the
  editor so dirty state stays current.
- Nested reference-type generated models are exposed through cached child proxies, so
  edits such as `Value.Database.Host = ...` are observable.
- Collections expose the underlying value with explicit **replace-only** semantics:
  element mutations are not tracked. Assign a new collection to raise notifications.
- Struct child models have value semantics and are exposed as replaceable scalars.

## Read-only binding

```csharp
var reader = new ConfiglueStateReader<AppSettings>(
    state,                                    // IReadOnlyState<AppSettings>
    dispatcher: dispatcher,                   // IConfiglueDispatcher
    diagnostics: diagnostics                  // optional IConfiglueDiagnostics<AppSettings>
);

await reader.InitializeAsync();

reader.Value;          // bindable value (generated proxy)
reader.Snapshot;       // StateSnapshot<AppSettings>, call .GetDetails() for provenance
reader.IsLoading;
reader.HasValue;
reader.LoadFailure;    // initial load failure
reader.ReloadFailure;  // most recent reload failure; last known-good value is retained
```

A failed reload keeps the previously rendered value and only surfaces `ReloadFailure`.
Effective-value changes are re-resolved as a whole snapshot so the value and its details
never disagree.

## Editable binding

```csharp
var editor = new ConfiglueStateEditor<AppSettings>(
    editSessions,                             // IConfiglueEditSessions<AppSettings>
    dispatcher: dispatcher,
    diagnostics: diagnostics,
    subjectChangeSource: subjectChangeSource
);

await editor.InitializeAsync();

editor.Value;                 // bindable draft (generated proxy)
editor.SessionStart;          // snapshot; call .GetDetails() for editability/provenance
editor.IsDirty;               // EditSession.HasLocalChanges
editor.IsSaving;
editor.HasUpstreamChanges;
editor.IsSubjectChanged;
editor.HasErrors;             // INotifyDataErrorInfo
editor.ValidationFailures;
editor.LastException;

await editor.SaveAsync();     // EditSession.CommitAsync; always asynchronous
await editor.RebaseAsync();
editor.ResetToUpstream();
editor.ResetToSessionStart();
editor.ResetToDefault();
```

Clean sessions follow Core auto-rebase semantics: when upstream changes and the draft has
no local changes, the draft is rebased automatically. Dirty drafts are preserved and
`HasUpstreamChanges` is surfaced instead of silently rebasing.

Validation is surfaced through `INotifyDataErrorInfo` (`HasErrors`, `GetErrors`,
`ErrorsChanged`). `IDataErrorInfo` is implemented only as a compatibility bridge for older
binding stacks; Configlue validation is model-level, so the same failures are returned for
every column. Persistence is never made synchronous to satisfy `IEditableObject`; if a
WinForms/WPF bridge uses `IEditableObject`, treat it as a local edit transaction and call
`SaveAsync` explicitly.

## UI dispatch

Reload, upstream, and subject notifications may arrive on background threads. Provide an
`IConfiglueDispatcher` so observable updates are marshaled to the UI thread. When no
dispatcher is supplied, the adapter captures the current `SynchronizationContext` (falling
back to inline execution).

```csharp
// WPF
sealed class WpfDispatcher(System.Windows.Threading.Dispatcher dispatcher) : IConfiglueDispatcher
{
    public bool CheckAccess() => dispatcher.CheckAccess();
    public void Post(Action action) => dispatcher.InvokeAsync(action);
}

// WinForms
sealed class WinFormsDispatcher(System.Windows.Forms.Control control) : IConfiglueDispatcher
{
    public bool CheckAccess() => !control.InvokeRequired;
    public void Post(Action action) => control.BeginInvoke(action);
}

// Avalonia
sealed class AvaloniaDispatcher : IConfiglueDispatcher
{
    public bool CheckAccess() => Avalonia.Threading.Dispatcher.UIThread.CheckAccess();
    public void Post(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action);
}
```

The same shape applies to MAUI (`MainThread`), WinUI (`DispatcherQueue`), Unity, and Godot.
Core and this package never reference those frameworks.

## Async save commands

Expose save as an asynchronous command instead of an `ICommand` that blocks:

```csharp
sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute();
    public async void Execute(object? parameter) => await execute();
    // raise CanExecuteChanged when editor.IsDirty/IsSaving change (subscribe to PropertyChanged)
}
```

Bind `CanExecute` to `editor.IsDirty && !editor.IsSaving` and raise `CanExecuteChanged`
from `editor.PropertyChanged`.
