using System.Windows.Forms;
using Configlue.Extensions.ComponentModel;

namespace Configlue.Hosting.WinForms;

/// <summary>Dispatches ComponentModel updates through an application-owned Windows Forms control.</summary>
/// <remarks>
/// Construct this adapter on the control's owning UI thread after its handle has been created.
/// The adapter does not create or own the control or its handle. Posts made while the handle
/// is absent, being destroyed, or disposed fail explicitly. Accepted callbacks follow the
/// control's native message queue and handle recreation/shutdown behavior.
/// </remarks>
public sealed class WinFormsConfiglueDispatcher : IConfiglueDispatcher
{
    private readonly Control _anchor;
    private readonly int _threadId;

    /// <summary>Creates an adapter on the UI thread owning an initialized control.</summary>
    /// <param name="anchor">An application-owned control with a created handle.</param>
    /// <exception cref="InvalidOperationException">The handle is absent or the caller is not its UI thread.</exception>
    /// <exception cref="ObjectDisposedException">The control is disposing or disposed.</exception>
    public WinFormsConfiglueDispatcher(Control anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        EnsureAvailable(anchor);
        if (anchor.InvokeRequired)
            throw new InvalidOperationException(
                "Create the dispatcher on the control's owning UI thread."
            );
        _anchor = anchor;
        _threadId = Environment.CurrentManagedThreadId;
    }

    /// <inheritdoc />
    public bool CheckAccess() =>
        Environment.CurrentManagedThreadId == _threadId
        && !_anchor.IsDisposed
        && !_anchor.Disposing
        && _anchor.IsHandleCreated;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The control has no handle or cannot accept the callback.</exception>
    /// <exception cref="ObjectDisposedException">The control is disposing or disposed.</exception>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsureAvailable(_anchor);
        _anchor.BeginInvoke(action);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The control has no handle or cannot accept the callback.</exception>
    /// <exception cref="ObjectDisposedException">The control is disposing or disposed.</exception>
    public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default) =>
        ConfiglueDispatcher.InvokeAsync(this, action, cancellationToken);

    private static void EnsureAvailable(Control anchor)
    {
        if (anchor.IsDisposed || anchor.Disposing)
            throw new ObjectDisposedException(nameof(anchor));
        if (!anchor.IsHandleCreated)
            throw new InvalidOperationException(
                "The dispatcher control must have a created handle."
            );
    }
}
