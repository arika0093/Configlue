using Configlue.State;

namespace Configlue;

// Advanced diagnostics surface: signals that sources were reloaded (revisions changed) even though
// the resolved effective model was unchanged, for example because the changed contribution is
// shadowed by a higher-priority source. Effective-value consumers should observe IReadOnlyState.OnChange.
internal interface IConfiglueReloadDiagnostics
{
    /// <summary>Subscribes to reloads whose effective resolved value did not change.</summary>
    IDisposable OnReload(Action<StateRevisionVector?> listener);
}
