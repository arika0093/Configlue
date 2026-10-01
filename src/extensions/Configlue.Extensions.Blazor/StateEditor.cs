using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Extensions.Blazor;

/// <summary>
/// Headless component that owns a Core <see cref="EditSession{T}"/> and exposes it to standard
/// Blazor form controls through an <see cref="EditContext"/>.
/// </summary>
/// <typeparam name="T">The configuration model type.</typeparam>
/// <remarks>
/// The component does not render its own form. Child content is expected to supply an
/// <c>EditForm</c> bound to <see cref="StateEditorContext{T}.EditContext"/>.
/// </remarks>
public sealed class StateEditor<T> : ComponentBase, IDisposable
    where T : class
{
    private readonly StateEditorContext<T> _context;
    private EditSession<T>? _session;
    private EditContext? _editContext;
    private IDisposable? _subjectSubscription;
    private IDisposable? _reloadFailureSubscription;
    private int _disposed;
    private bool _isSaving;

    /// <summary>Creates an editor component.</summary>
    public StateEditor() => _context = new StateEditorContext<T>(this);

    /// <summary>The service provider used for optional subject-change resolution.</summary>
    [Inject]
    public IServiceProvider Services { get; set; } = default!;

    /// <summary>The Core edit-session factory for this model.</summary>
    [Inject]
    public IConfiglueEditSessions<T> EditSessions { get; set; } = default!;

    /// <summary>Child content rendered with the current edit context.</summary>
    [Parameter]
    public RenderFragment<StateEditorContext<T>>? ChildContent { get; set; }

    /// <summary>Content rendered while the edit session is opening.</summary>
    [Parameter]
    public RenderFragment? LoadingContent { get; set; }

    /// <summary>Content rendered when the edit session fails to open.</summary>
    [Parameter]
    public RenderFragment<Exception>? LoadFailedContent { get; set; }

    /// <summary>
    /// Whether a clean session automatically rebases when upstream changes. Defaults to <c>true</c>.
    /// Dirty drafts are never overwritten.
    /// </summary>
    [Parameter]
    public bool AutoRebaseOnCleanUpstreamChange { get; set; } = true;

    /// <summary>
    /// Whether saving a draft after the current subject changed is allowed. Defaults to <c>false</c>
    /// so an old subject's draft is never written accidentally through a UI representing a new subject.
    /// </summary>
    [Parameter]
    public bool AllowSavingInvalidatedDraft { get; set; }

    /// <summary>Receives categorized operation failures while retaining the underlying Core exception.</summary>
    [Parameter]
    public EventCallback<StateEditorErrorEventArgs<T>> OnError { get; set; }

    /// <summary>Raised when the current subject changes while this editor is open.</summary>
    [Parameter]
    public EventCallback<StateEditorSubjectChangedEventArgs> OnSubjectChanged { get; set; }

    internal EditSession<T>? Session => _session;

    internal EditContext? CurrentEditContext => _editContext;

    internal bool IsLoading { get; private set; } = true;

    internal bool IsSaving => _isSaving;

    internal bool HasUpstreamChanges => _session?.HasUpstreamChanges ?? false;

    internal bool IsSubjectChanged { get; private set; }

    internal Exception? LoadFailure { get; private set; }

    internal StateWriteReceipt? LastReceipt { get; private set; }

    internal StateEditorErrorEventArgs<T>? LastError { get; private set; }

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        _subjectSubscription = Services
            .GetService<IConfiglueSubjectChangeSource>()
            ?.OnChange(OnSubjectChangeSignaled);
        if (
            Services.GetService<IConfiglueDiagnostics<T>>()
            is IConfiglueReloadFailureDiagnostics<T> diagnostics
        )
        {
            _reloadFailureSubscription = diagnostics.OnReloadFailed(OnReloadFailureReported);
        }

        await OpenSessionAsync().ConfigureAwait(true);
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (_session is not null && _editContext is not null)
        {
            if (ChildContent is not null)
            {
                builder.AddContent(0, ChildContent(_context));
            }

            return;
        }

        if (IsLoading)
        {
            if (LoadingContent is not null)
            {
                builder.AddContent(1, LoadingContent);
            }

            return;
        }

        if (LoadFailure is not null && LoadFailedContent is not null)
        {
            builder.AddContent(2, LoadFailedContent(LoadFailure));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _subjectSubscription?.Dispose();
        _reloadFailureSubscription?.Dispose();
        if (_session is not null)
        {
            _session.UpstreamChanged -= OnUpstreamChanged;
            _session.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    internal async ValueTask SaveAsync(CancellationToken cancellationToken)
    {
        if (_session is null || _isSaving)
        {
            return;
        }

        if (IsSubjectChanged && !AllowSavingInvalidatedDraft)
        {
            RaiseError(
                StateEditorOperation.SubjectChange,
                new InvalidOperationException(
                    "The current subject changed while this editor held unsaved changes."
                ),
                StateEditorErrorKind.SubjectChanged
            );
            return;
        }

        _isSaving = true;
        RaiseStateChanged();
        try
        {
            LastReceipt = await _session.CommitAsync(cancellationToken).ConfigureAwait(true);
            if (Volatile.Read(ref _disposed) == 0)
            {
                _editContext?.MarkAsUnmodified();
                _editContext?.NotifyValidationStateChanged();
            }
        }
        catch (Exception exception)
        {
            RaiseError(StateEditorOperation.Save, exception);
        }
        finally
        {
            _isSaving = false;
            RaiseStateChanged();
        }
    }

    internal async ValueTask RebaseAsync(CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            await _session.RebaseAsync(cancellationToken).ConfigureAwait(true);
            RebuildEditContext();
        }
        catch (Exception exception)
        {
            RaiseError(StateEditorOperation.Rebase, exception);
        }
        finally
        {
            RaiseStateChanged();
        }
    }

    internal void ResetToUpstream()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            _session.ResetToUpstream();
            RebuildEditContext();
        }
        catch (Exception exception)
        {
            RaiseError(StateEditorOperation.Reset, exception);
        }
        finally
        {
            RaiseStateChanged();
        }
    }

    internal void ResetToSessionStart()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            _session.ResetToSessionStart();
            RebuildEditContext();
        }
        catch (Exception exception)
        {
            RaiseError(StateEditorOperation.Reset, exception);
        }
        finally
        {
            RaiseStateChanged();
        }
    }

    internal void ResetToDefault()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            _session.ResetToDefault();
            RebuildEditContext();
        }
        catch (Exception exception)
        {
            RaiseError(StateEditorOperation.Reset, exception);
        }
        finally
        {
            RaiseStateChanged();
        }
    }

    private async Task OpenSessionAsync()
    {
        try
        {
            var session = await EditSessions.OpenEditSessionAsync().ConfigureAwait(true);
            if (Volatile.Read(ref _disposed) != 0)
            {
                session.Dispose();
                return;
            }

            _session = session;
            _editContext = new EditContext(session.Value);
            session.UpstreamChanged += OnUpstreamChanged;
            LoadFailure = null;
        }
        catch (Exception exception)
        {
            LoadFailure = exception;
            RaiseError(StateEditorOperation.Load, exception);
        }
        finally
        {
            IsLoading = false;
            RaiseStateChanged();
        }
    }

    private void OnUpstreamChanged() => _ = InvokeAsync(HandleUpstreamChangedAsync);

    private async Task HandleUpstreamChangedAsync()
    {
        if (_disposed != 0 || _session is null || _isSaving)
        {
            return;
        }

        if (!_session.HasLocalChanges && AutoRebaseOnCleanUpstreamChange)
        {
            await RebaseAsync(CancellationToken.None).ConfigureAwait(true);
            return;
        }

        RaiseStateChanged();
    }

    private void OnReloadFailureReported(Exception exception) =>
        _ = InvokeAsync(() =>
            RaiseError(StateEditorOperation.Reload, exception, StateEditorErrorKind.Reload)
        );

    private void OnSubjectChangeSignaled() => _ = InvokeAsync(HandleSubjectChangedAsync);

    private async Task HandleSubjectChangedAsync()
    {
        if (_disposed != 0 || _session is null)
        {
            return;
        }

        var hadUnsavedChanges = _session.HasLocalChanges;
        if (hadUnsavedChanges)
        {
            IsSubjectChanged = true;
            RaiseStateChanged();
            await OnSubjectChanged
                .InvokeAsync(new StateEditorSubjectChangedEventArgs(true))
                .ConfigureAwait(true);
            return;
        }

        IsLoading = true;
        RaiseStateChanged();
        _session.UpstreamChanged -= OnUpstreamChanged;
        _session.Dispose();
        _session = null;
        _editContext = null;
        IsSubjectChanged = false;
        await OpenSessionAsync().ConfigureAwait(true);
        await OnSubjectChanged
            .InvokeAsync(new StateEditorSubjectChangedEventArgs(false))
            .ConfigureAwait(true);
    }

    private void RebuildEditContext()
    {
        if (_session is not null)
        {
            _editContext = new EditContext(_session.Value);
        }
    }

    private void RaiseError(
        StateEditorOperation operation,
        Exception exception,
        StateEditorErrorKind? kind = null
    )
    {
        var args = new StateEditorErrorEventArgs<T>(
            operation,
            kind ?? Classify(exception),
            exception
        );
        LastError = args;
        if (Volatile.Read(ref _disposed) == 0)
        {
            _ = OnError.InvokeAsync(args);
        }
    }

    private void RaiseStateChanged()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            StateHasChanged();
        }
    }

    private static StateEditorErrorKind Classify(Exception exception) =>
        exception switch
        {
            ConfiglueValidationException => StateEditorErrorKind.Validation,
            StateConflictException => StateEditorErrorKind.Conflict,
            StateMultiWriteException => StateEditorErrorKind.MultiWrite,
            _ => StateEditorErrorKind.Unknown,
        };
}
