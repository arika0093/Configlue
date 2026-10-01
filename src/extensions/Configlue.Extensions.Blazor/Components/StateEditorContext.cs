using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Configlue.Extensions.Blazor;

/// <summary>Edit context exposed to <see cref="StateEditor{T}"/> child content.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public sealed class StateEditorContext<T>
    where T : class
{
    private readonly StateEditor<T> _owner;

    internal StateEditorContext(StateEditor<T> owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    /// <summary>The current editable draft value owned by the Core edit session.</summary>
    public T Value => _owner.Session is null ? default! : _owner.Session.Value;

    /// <summary>The underlying Core edit session.</summary>
    public EditSession<T> Session => _owner.Session!;

    /// <summary>The Blazor edit context tracking the current draft.</summary>
    public EditContext EditContext => _owner.CurrentEditContext!;

    /// <summary>The snapshot captured when the edit session was opened.</summary>
    public StateSnapshot<T> SessionStart => Session.SessionStart;

    /// <summary>Whether a save is currently in flight.</summary>
    public bool IsSaving => _owner.IsSaving;

    /// <summary>Whether the edit session is still being opened.</summary>
    public bool IsLoading => _owner.IsLoading;

    /// <summary>Whether upstream changed while the draft is dirty.</summary>
    public bool HasUpstreamChanges => _owner.HasUpstreamChanges;

    /// <summary>Whether the draft differs from its baseline.</summary>
    public bool HasLocalChanges => _owner.Session?.HasLocalChanges ?? false;

    /// <summary>Whether the current subject changed while the editor was open.</summary>
    public bool IsSubjectChanged => _owner.IsSubjectChanged;

    /// <summary>The receipt from the most recent successful save.</summary>
    public StateWriteReceipt? LastReceipt => _owner.LastReceipt;

    /// <summary>The categorized failure from the most recent operation.</summary>
    public StateEditorErrorEventArgs<T>? LastError => _owner.LastError;

    /// <summary>The failure that prevented the edit session from opening.</summary>
    public Exception? LoadFailure => _owner.LoadFailure;

    /// <summary>Commits the current draft through the Core edit session.</summary>
    /// <param name="cancellationToken">A token that can cancel the save.</param>
    public ValueTask SaveAsync(CancellationToken cancellationToken = default) =>
        _owner.SaveAsync(cancellationToken);

    /// <summary>Resolves upstream and reapplies local draft changes through the Core session.</summary>
    /// <param name="cancellationToken">A token that can cancel the rebase.</param>
    public ValueTask RebaseAsync(CancellationToken cancellationToken = default) =>
        _owner.RebaseAsync(cancellationToken);

    /// <summary>Discards local changes in favor of the latest known upstream snapshot.</summary>
    public void ResetToUpstream() => _owner.ResetToUpstream();

    /// <summary>Restores the value loaded when the session was opened.</summary>
    public void ResetToSessionStart() => _owner.ResetToSessionStart();

    /// <summary>Resets the draft to the model default value.</summary>
    public void ResetToDefault() => _owner.ResetToDefault();
}
