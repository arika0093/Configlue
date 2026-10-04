using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Hosting.Blazor;

/// <summary>
/// Headless component that owns a Core <see cref="EditSession{T}"/> and exposes it to standard
/// Blazor form controls through an <see cref="EditContext"/>.
/// </summary>
/// <typeparam name="T">The configuration model type.</typeparam>
/// <remarks>
/// The component does not render its own form. Child content is expected to supply an
/// <c>EditForm</c> bound to <see cref="StateEditorContext{T}.EditContext"/>.
/// <para>
/// Edit sessions are never persisted across the prerender/interactive boundary: every interactive
/// render opens a fresh session so a half-edited draft cannot leak from prerender into the
/// browser session.
/// </para>
/// </remarks>
public sealed partial class StateEditor<T> : ComponentBase, IDisposable
    where T : class
{
    private readonly StateEditorContext<T> _context;
    private EditSession<T>? _session;
    private EditContext? _editContext;
    private IDisposable? _subjectSubscription;
    private IDisposable? _reloadFailureSubscription;
    private int _disposed;
    private bool _isSaving;
    private long _subjectGeneration;
    private long _observedGeneration;
    private int _sessionWorkerRunning;
    private int _subjectNotificationPending;

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

        Interlocked.Increment(ref _subjectGeneration);
        await EnsureSessionWorkerAsync().ConfigureAwait(true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _subjectGeneration);
        _subjectSubscription?.Dispose();
        _reloadFailureSubscription?.Dispose();
        if (_session is not null)
        {
            _session.UpstreamChanged -= OnUpstreamChanged;
            _session.Dispose();
        }

        _session = null;
        _editContext = null;
        _isSaving = false;
        GC.SuppressFinalize(this);
    }

    internal async ValueTask SaveAsync(CancellationToken cancellationToken)
    {
        var session = _session;
        var editContext = _editContext;
        var generation = Interlocked.Read(ref _subjectGeneration);
        if (Volatile.Read(ref _disposed) != 0 || session is null || _isSaving)
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
            var receipt = await session.CommitAsync(cancellationToken).ConfigureAwait(true);
            if (IsCurrentSession(session, generation))
            {
                LastReceipt = receipt;
                editContext?.MarkAsUnmodified();
                editContext?.NotifyValidationStateChanged();
            }
        }
        catch (Exception exception)
        {
            if (IsCurrentSession(session, generation))
            {
                RaiseError(StateEditorOperation.Save, exception);
            }
        }
        finally
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _isSaving = false;
                RaiseStateChanged();
            }
        }
    }

    internal async ValueTask RebaseAsync(CancellationToken cancellationToken)
    {
        var session = _session;
        var generation = Interlocked.Read(ref _subjectGeneration);
        if (Volatile.Read(ref _disposed) != 0 || session is null)
        {
            return;
        }

        try
        {
            await session.RebaseAsync(cancellationToken).ConfigureAwait(true);
            if (IsCurrentSession(session, generation))
            {
                RebuildEditContext();
            }
        }
        catch (Exception exception)
        {
            if (IsCurrentSession(session, generation))
            {
                RaiseError(StateEditorOperation.Rebase, exception);
            }
        }
        finally
        {
            if (IsCurrentSession(session, generation))
            {
                RaiseStateChanged();
            }
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

    private async Task EnsureSessionWorkerAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _sessionWorkerRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            while (true)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                var generation = Interlocked.Read(ref _subjectGeneration);
                var session = _session;
                if (session is not null)
                {
                    if (session.HasLocalChanges)
                    {
                        IsSubjectChanged = true;
                        Interlocked.Exchange(
                            ref _observedGeneration,
                            Interlocked.Read(ref _subjectGeneration)
                        );
                        RaiseStateChanged();
                        await NotifySubjectChangedAsync(hasUnsavedChanges: true)
                            .ConfigureAwait(true);
                        return;
                    }

                    session.UpstreamChanged -= OnUpstreamChanged;
                    _session = null;
                    _editContext = null;
                    session.Dispose();
                }

                IsLoading = true;
                IsSubjectChanged = false;
                LoadFailure = null;
                RaiseStateChanged();

                EditSession<T> opened;
                try
                {
                    opened = await EditSessions.OpenEditSessionAsync().ConfigureAwait(true);
                }
                catch (Exception exception)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        return;
                    }

                    if (generation != Interlocked.Read(ref _subjectGeneration))
                    {
                        continue;
                    }

                    LoadFailure = exception;
                    IsLoading = false;
                    Interlocked.Exchange(ref _observedGeneration, generation);
                    RaiseError(StateEditorOperation.Load, exception);
                    RaiseStateChanged();
                    await NotifySubjectChangedAsync(hasUnsavedChanges: false).ConfigureAwait(true);
                    return;
                }

                if (Volatile.Read(ref _disposed) != 0)
                {
                    opened.Dispose();
                    return;
                }

                if (generation != Interlocked.Read(ref _subjectGeneration))
                {
                    opened.Dispose();
                    continue;
                }

                _session = opened;
                _editContext = new EditContext(opened.Value);
                opened.UpstreamChanged += OnUpstreamChanged;
                LoadFailure = null;
                IsLoading = false;
                IsSubjectChanged = false;
                Interlocked.Exchange(ref _observedGeneration, generation);
                RaiseStateChanged();
                await NotifySubjectChangedAsync(hasUnsavedChanges: false).ConfigureAwait(true);
                return;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _sessionWorkerRunning, 0);
            if (
                Volatile.Read(ref _disposed) == 0
                && Interlocked.Read(ref _subjectGeneration)
                    != Interlocked.Read(ref _observedGeneration)
            )
            {
                _ = InvokeAsync(EnsureSessionWorkerAsync);
            }
        }
    }

    private async ValueTask NotifySubjectChangedAsync(bool hasUnsavedChanges)
    {
        if (
            Volatile.Read(ref _disposed) != 0
            || Interlocked.Exchange(ref _subjectNotificationPending, 0) == 0
        )
        {
            return;
        }

        await OnSubjectChanged
            .InvokeAsync(new StateEditorSubjectChangedEventArgs(hasUnsavedChanges))
            .ConfigureAwait(true);
    }

    private void OnUpstreamChanged()
    {
        var session = _session;
        var generation = Interlocked.Read(ref _subjectGeneration);
        if (session is not null)
        {
            _ = InvokeAsync(() => HandleUpstreamChangedAsync(session, generation));
        }
    }

    private async Task HandleUpstreamChangedAsync(EditSession<T> session, long generation)
    {
        if (!IsCurrentSession(session, generation) || _isSaving)
        {
            return;
        }

        if (!session.HasLocalChanges && AutoRebaseOnCleanUpstreamChange)
        {
            await RebaseAsync(CancellationToken.None).ConfigureAwait(true);
            return;
        }

        RaiseStateChanged();
    }

    private void OnReloadFailureReported(Exception exception)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _ = InvokeAsync(() =>
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            RaiseError(StateEditorOperation.Reload, exception, StateEditorErrorKind.Reload);
        });
    }

    private void OnSubjectChangeSignaled()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _subjectGeneration);
        Interlocked.Exchange(ref _subjectNotificationPending, 1);
        _ = InvokeAsync(EnsureSessionWorkerAsync);
    }

    private bool IsCurrentSession(EditSession<T> session, long generation) =>
        Volatile.Read(ref _disposed) == 0
        && ReferenceEquals(session, _session)
        && generation == Interlocked.Read(ref _subjectGeneration);

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
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

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
