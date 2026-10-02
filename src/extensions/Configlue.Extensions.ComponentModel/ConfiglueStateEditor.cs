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
/// Persistence stays asynchronous. Asynchronous operations complete only after their
/// bindable state transition has been applied on the dispatcher, and session-open
/// generations ensure a superseded open cannot attach after a newer one (or disposal) won.
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
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly CancellationToken _lifetimeToken;
    private Task? _initializationTask;
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
    private int _generation;
    private int _saveGate;
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
        _lifetimeToken = _lifetimeCancellation.Token;
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
    /// <returns>A task that completes when the session has been attached on the dispatcher.</returns>
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initializationTask is null)
            {
                var completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                _initializationTask = completion.Task;
                _ = InitializeCoreAsync(completion, cancellationToken);
            }
            return new ValueTask(_initializationTask);
        }
    }

    private async Task InitializeCoreAsync(
        TaskCompletionSource<bool> completion,
        CancellationToken cancellationToken
    )
    {
        try
        {
            _subjectSubscription = _subjectChangeSource?.OnChange(OnSubjectChangeSignaled);
            if (_diagnostics is IConfiglueReloadFailureDiagnostics<T> diagnostics)
                _reloadFailureSubscription = diagnostics.OnReloadFailed(OnReloadFailureReported);
            await OpenSessionAsync(cancellationToken).ConfigureAwait(false);
            completion.TrySetResult(true);
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    /// <summary>Commits the current draft through the Core edit session.</summary>
    /// <param name="cancellationToken">A token that can cancel the save.</param>
    /// <returns>A task that completes when the save and its bindable transition are complete.</returns>
    public async ValueTask SaveAsync(CancellationToken cancellationToken = default)
    {
        var session = _session;
        if (session is null || Interlocked.CompareExchange(ref _saveGate, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (_isSubjectChanged && !AllowSavingInvalidatedDraft)
            {
                await InvokeAsync(
                        () =>
                            SetError(
                                new InvalidOperationException(
                                    "The current subject changed while this editor held unsaved changes."
                                )
                            ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return;
            }

            await InvokeAsync(
                    () =>
                    {
                        _isSaving = true;
                        Raise(nameof(IsSaving));
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
            try
            {
                var receipt = await session.CommitAsync(cancellationToken).ConfigureAwait(false);
                await InvokeAsync(
                        () =>
                        {
                            if (_disposed || !ReferenceEquals(_session, session))
                            {
                                return;
                            }

                            _lastReceipt = receipt;
                            ClearErrors();
                            ResetProxy();
                            Raise(nameof(LastReceipt));
                        },
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await InvokeAsync(
                        () =>
                        {
                            if (!_disposed && ReferenceEquals(_session, session))
                            {
                                SetError(exception);
                            }
                        },
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            }
            finally
            {
                await InvokeAsync(
                        () =>
                        {
                            _isSaving = false;
                            if (!_disposed)
                            {
                                Raise(nameof(IsSaving));
                                Raise(nameof(IsDirty));
                            }
                        },
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _saveGate, 0);
        }
    }

    /// <summary>Resolves upstream and reapplies local draft changes through the Core session.</summary>
    /// <param name="cancellationToken">A token that can cancel the rebase.</param>
    /// <returns>A task that completes when the rebase and its bindable transition are complete.</returns>
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
            await InvokeAsync(
                    () =>
                    {
                        if (!_disposed && ReferenceEquals(_session, session))
                        {
                            ResetProxy();
                        }
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await InvokeAsync(
                    () =>
                    {
                        if (!_disposed && ReferenceEquals(_session, session))
                        {
                            SetError(exception);
                        }
                    },
                    CancellationToken.None
                )
                .ConfigureAwait(false);
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
        IDisposable? subjects;
        IDisposable? failures;
        EditSession<T>? session;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            Interlocked.Increment(ref _generation);
            subjects = _subjectSubscription;
            failures = _reloadFailureSubscription;
            session = _session;
            _subjectSubscription = null;
            _reloadFailureSubscription = null;
            _session = null;
            _value = null;
            _isSaving = false;
        }
        _lifetimeCancellation.Cancel();
        subjects?.Dispose();
        failures?.Dispose();
        if (session is not null)
        {
            session.UpstreamChanged -= OnUpstreamChanged;
            session.Dispose();
        }
        _lifetimeCancellation.Dispose();
    }

    private async ValueTask OpenSessionAsync(
        CancellationToken cancellationToken,
        int? requestedGeneration = null
    )
    {
        var generation = requestedGeneration ?? Interlocked.Increment(ref _generation);
        EditSession<T>? session = null;
        Exception? failure = null;
        try
        {
            session = await _editSessions
                .OpenEditSessionAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await InvokeAsync(
                    () =>
                    {
                        if (_disposed || generation != Volatile.Read(ref _generation))
                        {
                            session?.Dispose();
                            return;
                        }

                        if (failure is not null)
                        {
                            _loadFailure = failure;
                            _lastException = failure;
                            Raise(nameof(LoadFailure));
                            Raise(nameof(LastException));
                        }
                        else
                        {
                            AttachSession(session!);
                            _loadFailure = null;
                            Raise(nameof(LoadFailure));
                        }

                        if (_isLoading)
                        {
                            _isLoading = false;
                            Raise(nameof(IsLoading));
                        }
                    },
                    cancellationToken,
                    includeDisposed: true
                )
                .ConfigureAwait(false);
        }
        catch
        {
            session?.Dispose();
            throw;
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
            if (_disposed)
            {
                return;
            }

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

        RunOnDispatcher(() =>
        {
            if (_disposed)
            {
                return;
            }

            _ = HandleUpstreamChangedAsync();
        });
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

        RunOnDispatcher(() =>
        {
            if (!_disposed)
            {
                SetError(exception);
            }
        });
    }

    private void OnSubjectChangeSignaled()
    {
        if (_disposed)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _generation);
        RunOnDispatcher(() =>
        {
            if (_disposed || generation != Volatile.Read(ref _generation))
            {
                return;
            }

            _ = HandleSubjectChangeAsync(generation);
        });
    }

    private async Task HandleSubjectChangeAsync(int generation)
    {
        var session = _session;
        if (_disposed)
        {
            return;
        }

        if (session?.HasLocalChanges == true)
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
        try
        {
            await OpenSessionAsync(CancellationToken.None, generation).ConfigureAwait(false);
        }
        catch (Exception) when (_lifetimeToken.IsCancellationRequested)
        {
            // Disposal cancels pending notification work.
        }
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

    private async ValueTask InvokeAsync(
        Action action,
        CancellationToken cancellationToken,
        bool includeDisposed = false
    )
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeToken
        );
        await _dispatcher
            .InvokeAsync(
                () =>
                {
                    lock (_gate)
                        if (includeDisposed || !_disposed)
                            action();
                },
                linked.Token
            )
            .ConfigureAwait(false);
    }

    private void RunOnDispatcher(Action action)
    {
        void GuardedAction()
        {
            lock (_gate)
                if (!_disposed)
                    action();
        }
        if (_dispatcher.CheckAccess())
            GuardedAction();
        else
            _dispatcher.Post(GuardedAction);
    }

    private void Raise(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
