using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace Configlue.Extensions.ComponentModel;

/// <summary>
/// A bindable, host-neutral editable view over an <see cref="EditSession{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The editor exposes a bindable proxy over <see cref="EditSession{T}.Value"/> so that
/// root and nested structural property changes raise notifications and update dirty state,
/// without requiring user models to implement <see cref="INotifyPropertyChanged"/>.
/// </para>
/// <para>
/// Clean sessions follow Core auto-rebase semantics: when upstream changes and the draft
/// has no local changes, the draft is rebased automatically. Dirty drafts are preserved
/// and <see cref="HasUpstreamChanges"/> is surfaced instead of silently rebasing.
/// </para>
/// <para>
/// Persistence stays asynchronous. <see cref="IDataErrorInfo"/> is provided only as a
/// compatibility bridge; <see cref="INotifyDataErrorInfo"/> is the primary validation API.
/// </para>
/// </remarks>
/// <typeparam name="T">The configuration model type.</typeparam>
public sealed class ConfiglueStateEditor<T>
    : INotifyPropertyChanged,
        INotifyDataErrorInfo,
        IDataErrorInfo,
        IDisposable
    where T : class
{
    private readonly IConfiglueEditSessions<T> _editSessions;
    private readonly IConfiglueDispatcher _dispatcher;
    private readonly IConfiglueDiagnostics<T>? _diagnostics;
    private readonly IConfiglueSubjectChangeSource? _subjectChangeSource;
    private readonly List<string> _validationFailures = [];
    private IDisposable? _subjectSubscription;
    private IDisposable? _reloadFailureSubscription;
    private EditSession<T>? _session;
    private object? _value;
    private bool _isLoading = true;
    private bool _isSaving;
    private bool _isSubjectChanged;
    private Exception? _loadFailure;
    private Exception? _lastException;
    private StateWriteReceipt? _lastReceipt;
    private bool _disposed;

    /// <summary>Creates a bindable editor.</summary>
    /// <param name="editSessions">The Core edit-session factory for this model.</param>
    /// <param name="dispatcher">
    /// The dispatcher used to marshal notifications. Defaults to the current
    /// <see cref="SynchronizationContext"/> when present.
    /// </param>
    /// <param name="diagnostics">Optional diagnostics used to observe reload failures.</param>
    /// <param name="subjectChangeSource">Optional subject-change signal source.</param>
    public ConfiglueStateEditor(
        IConfiglueEditSessions<T> editSessions,
        IConfiglueDispatcher? dispatcher = null,
        IConfiglueDiagnostics<T>? diagnostics = null,
        IConfiglueSubjectChangeSource? subjectChangeSource = null
    )
    {
        ArgumentNullException.ThrowIfNull(editSessions);
        _editSessions = editSessions;
        _dispatcher = dispatcher ?? ConfiglueDispatcher.FromCurrentSynchronizationContext();
        _diagnostics = diagnostics;
        _subjectChangeSource = subjectChangeSource;
    }

    /// <summary>
    /// The bindable draft. When the model has a generated proxy this is an observable
    /// proxy that tracks root and nested edits; otherwise it is the raw draft value.
    /// </summary>
    public object? Value => _value;

    /// <summary>The underlying Core edit session, or <see langword="null"/> before it opens.</summary>
    public EditSession<T>? Session => _session;

    /// <summary>
    /// The snapshot captured when the session opened. Call the generated
    /// <c>GetDetails()</c> extension on it to read provenance and editability.
    /// </summary>
    public StateSnapshot<T>? SessionStart => _session?.SessionStart;

    /// <summary>The latest known upstream snapshot, when one has been observed.</summary>
    public StateSnapshot<T>? LatestUpstream => _session?.LatestUpstream;

    /// <summary>Whether the initial edit session is still opening.</summary>
    public bool IsLoading => _isLoading;

    /// <summary>Whether a save is currently in flight.</summary>
    public bool IsSaving => _isSaving;

    /// <summary>Whether the draft differs from its baseline.</summary>
    public bool IsDirty => _session?.HasLocalChanges ?? false;

    /// <summary>Whether upstream changed while the draft is dirty.</summary>
    public bool HasUpstreamChanges => _session?.HasUpstreamChanges ?? false;

    /// <summary>Whether the current subject changed while the editor was open.</summary>
    public bool IsSubjectChanged => _isSubjectChanged;

    /// <summary>The receipt from the most recent successful save.</summary>
    public StateWriteReceipt? LastReceipt => _lastReceipt;

    /// <summary>The failure that prevented the edit session from opening.</summary>
    public Exception? LoadFailure => _loadFailure;

    /// <summary>The underlying exception from the most recent failed operation.</summary>
    public Exception? LastException => _lastException;

    /// <summary>The validation failures surfaced by the most recent failed save.</summary>
    public IReadOnlyList<string> ValidationFailures => _validationFailures.ToArray();

    /// <summary>
    /// Whether a clean session automatically rebases when upstream changes. Defaults to
    /// <see langword="true"/>. Dirty drafts are never overwritten.
    /// </summary>
    public bool AutoRebaseOnCleanUpstreamChange { get; set; } = true;

    /// <summary>
    /// Whether saving a draft after the current subject changed is allowed. Defaults to
    /// <see langword="false"/>.
    /// </summary>
    public bool AllowSavingInvalidatedDraft { get; set; }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc />
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    /// <inheritdoc />
    public bool HasErrors => _validationFailures.Count > 0;

    /// <summary>
    /// The aggregated validation error text. Provided for the
    /// <see cref="IDataErrorInfo"/> compatibility bridge.
    /// </summary>
    public string Error =>
        _validationFailures.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, _validationFailures);

    /// <summary>
    /// Gets the aggregated validation error text for a column. Configlue validation is
    /// model-level, so the same failures are surfaced for every column.
    /// </summary>
    /// <param name="columnName">The bound column name.</param>
    /// <returns>The aggregated validation error text.</returns>
    public string this[string columnName] => Error;

    /// <inheritdoc />
    public IEnumerable GetErrors(string? propertyName) => _validationFailures.ToArray();

    /// <summary>Opens the edit session and subscribes to upstream/subject notifications.</summary>
    /// <param name="cancellationToken">A token that can cancel the initial open.</param>
    /// <returns>A task that completes when the session is open.</returns>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session is not null)
        {
            return;
        }

        _subjectSubscription = _subjectChangeSource?.OnChange(OnSubjectChangeSignaled);
        if (_diagnostics is IConfiglueReloadFailureDiagnostics<T> diagnostics)
        {
            _reloadFailureSubscription = diagnostics.OnReloadFailed(OnReloadFailureReported);
        }

        await OpenSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Commits the current draft through the Core edit session.</summary>
    /// <param name="cancellationToken">A token that can cancel the save.</param>
    /// <returns>A task that completes when the save finishes.</returns>
    public async ValueTask SaveAsync(CancellationToken cancellationToken = default)
    {
        var session = _session;
        if (session is null || _isSaving)
        {
            return;
        }

        if (_isSubjectChanged && !AllowSavingInvalidatedDraft)
        {
            SetError(
                new InvalidOperationException(
                    "The current subject changed while this editor held unsaved changes."
                )
            );
            return;
        }

        _isSaving = true;
        Raise(nameof(IsSaving));
        try
        {
            var receipt = await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            RunOnDispatcher(() =>
            {
                _lastReceipt = receipt;
                ClearErrors();
                ResetProxy();
                Raise(nameof(LastReceipt));
            });
        }
        catch (Exception exception)
        {
            RunOnDispatcher(() => SetError(exception));
        }
        finally
        {
            _isSaving = false;
            RunOnDispatcher(() =>
            {
                Raise(nameof(IsSaving));
                Raise(nameof(IsDirty));
            });
        }
    }

    /// <summary>Resolves upstream and reapplies local draft changes through the Core session.</summary>
    /// <param name="cancellationToken">A token that can cancel the rebase.</param>
    /// <returns>A task that completes when the rebase finishes.</returns>
    public async ValueTask RebaseAsync(CancellationToken cancellationToken = default)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            await session.RebaseAsync(cancellationToken).ConfigureAwait(false);
            RunOnDispatcher(ResetProxy);
        }
        catch (Exception exception)
        {
            RunOnDispatcher(() => SetError(exception));
        }
    }

    /// <summary>Discards local changes in favor of the latest known upstream snapshot.</summary>
    public void ResetToUpstream() => Reset(static session => session.ResetToUpstream());

    /// <summary>Restores the value loaded when the session was opened.</summary>
    public void ResetToSessionStart() => Reset(static session => session.ResetToSessionStart());

    /// <summary>Resets the draft to the model default value.</summary>
    public void ResetToDefault() => Reset(static session => session.ResetToDefault());

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _subjectSubscription?.Dispose();
        _reloadFailureSubscription?.Dispose();
        _subjectSubscription = null;
        _reloadFailureSubscription = null;
        if (_session is not null)
        {
            _session.UpstreamChanged -= OnUpstreamChanged;
            _session.Dispose();
            _session = null;
        }

        _value = null;
    }

    private async ValueTask OpenSessionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var session = await _editSessions
                .OpenEditSessionAsync(cancellationToken)
                .ConfigureAwait(false);
            if (_disposed)
            {
                session.Dispose();
                return;
            }

            RunOnDispatcher(() =>
            {
                AttachSession(session);
                _loadFailure = null;
                Raise(nameof(LoadFailure));
            });
        }
        catch (Exception exception)
        {
            _loadFailure = exception;
            _lastException = exception;
            RunOnDispatcher(() =>
            {
                Raise(nameof(LoadFailure));
                Raise(nameof(LastException));
            });
        }
        finally
        {
            RunOnDispatcher(() =>
            {
                _isLoading = false;
                Raise(nameof(IsLoading));
            });
        }
    }

    private void AttachSession(EditSession<T> session)
    {
        _session = session;
        session.UpstreamChanged += OnUpstreamChanged;
        _value = ConfiglueBindableProxy.Create(typeof(T), session.Value, OnDraftChanged);
        Raise(nameof(Value));
        Raise(nameof(Session));
        Raise(nameof(SessionStart));
        Raise(nameof(IsDirty));
        Raise(nameof(HasUpstreamChanges));
    }

    private void DetachSession()
    {
        if (_session is null)
        {
            return;
        }

        _session.UpstreamChanged -= OnUpstreamChanged;
        _session.Dispose();
        _session = null;
        _value = null;
        Raise(nameof(Session));
        Raise(nameof(Value));
    }

    private void ResetProxy()
    {
        if (_session is null)
        {
            return;
        }

        _value = ConfiglueBindableProxy.Create(typeof(T), _session.Value, OnDraftChanged);
        Raise(nameof(Value));
        Raise(nameof(SessionStart));
        Raise(nameof(IsDirty));
        Raise(nameof(HasUpstreamChanges));
    }

    private void OnDraftChanged()
    {
        if (_disposed)
        {
            return;
        }

        RunOnDispatcher(() =>
        {
            Raise(nameof(IsDirty));
            Raise(nameof(HasUpstreamChanges));
        });
    }

    private void OnUpstreamChanged()
    {
        if (_disposed)
        {
            return;
        }

        RunOnDispatcher(() => _ = HandleUpstreamChangedAsync());
    }

    private async Task HandleUpstreamChangedAsync()
    {
        var session = _session;
        if (_disposed || session is null || _isSaving)
        {
            return;
        }

        if (!session.HasLocalChanges && AutoRebaseOnCleanUpstreamChange)
        {
            await RebaseAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        Raise(nameof(HasUpstreamChanges));
        Raise(nameof(IsDirty));
    }

    private void OnReloadFailureReported(Exception exception)
    {
        if (_disposed)
        {
            return;
        }

        RunOnDispatcher(() => SetError(exception));
    }

    private void OnSubjectChangeSignaled()
    {
        if (_disposed)
        {
            return;
        }

        RunOnDispatcher(() => _ = HandleSubjectChangeAsync());
    }

    private async Task HandleSubjectChangeAsync()
    {
        var session = _session;
        if (_disposed || session is null)
        {
            return;
        }

        if (session.HasLocalChanges)
        {
            _isSubjectChanged = true;
            Raise(nameof(IsSubjectChanged));
            return;
        }

        _isLoading = true;
        Raise(nameof(IsLoading));
        DetachSession();
        _isSubjectChanged = false;
        Raise(nameof(IsSubjectChanged));
        await OpenSessionAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void Reset(Action<EditSession<T>> operation)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            operation(session);
            ClearErrors();
            ResetProxy();
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
        finally
        {
            Raise(nameof(IsDirty));
            Raise(nameof(HasUpstreamChanges));
        }
    }

    private void SetError(Exception exception)
    {
        _lastException = exception;
        _validationFailures.Clear();
        if (exception is ConfiglueValidationException validation)
        {
            _validationFailures.AddRange(validation.Failures);
        }
        else
        {
            _validationFailures.Add(exception.Message);
        }

        Raise(nameof(HasErrors));
        Raise(nameof(Error));
        Raise(nameof(ValidationFailures));
        Raise(nameof(LastException));
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(string.Empty));
    }

    private void ClearErrors()
    {
        if (_validationFailures.Count == 0 && _lastException is null)
        {
            return;
        }

        _validationFailures.Clear();
        _lastException = null;
        Raise(nameof(HasErrors));
        Raise(nameof(Error));
        Raise(nameof(ValidationFailures));
        Raise(nameof(LastException));
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(string.Empty));
    }

    private void RunOnDispatcher(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.Post(action);
        }
    }

    private void Raise(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
