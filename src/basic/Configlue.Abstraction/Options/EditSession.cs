namespace Configlue;

/// <summary>The upstream snapshot resolved for a rebase, with the revisions that produced it.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
internal sealed record SessionUpstreamResolution<T>(
    StateSnapshot<T> Snapshot,
    StateRevisionVector? Revisions
);

/// <summary>A staged configuration edit that can be saved multiple times.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public sealed class EditSession<T> : IDisposable
{
    private const int OperationState = 1;
    private const int DisposedState = 2;

    private readonly Func<T, CancellationToken, ValueTask<StateCommitResult<T>>> _save;
    private readonly Func<T, T> _clone;
    private readonly Func<T, T, T, T> _rebase;
    private readonly Func<T, T, bool> _hasChanges;
    private readonly Func<
        CancellationToken,
        ValueTask<SessionUpstreamResolution<T>>
    >? _resolveRebaseUpstream;
    private readonly Action<SessionUpstreamResolution<T>>? _applyRebaseUpstream;
    private readonly StateSnapshot<T> _sessionStart;
    private readonly T _sessionStartValue;
    private readonly T _defaultValue;
    private readonly bool _hasSessionStartValue;
    private readonly bool _hasDefaultValue;
    private readonly object _upstreamGate = new();
    private IDisposable? _upstreamSubscription;
    private StateSnapshot<T>? _latestUpstream;
    private T _baseline;
    private bool _hasUpstreamChanges;
    private readonly UpstreamGenerationCounter _upstreamGeneration;
    private int _state;
    private int _isCommitted;

    /// <summary>Creates a configure session around a staged value and its save operation.</summary>
    /// <remarks>The initial value is the loaded baseline. Default resets require the baseline overload.</remarks>
    public EditSession(T value, Func<T, CancellationToken, ValueTask<StateWriteReceipt>> save)
        : this(
            value,
            new StateSnapshot<T>(value, null),
            AsCommitSave(save),
            static (_, desired, _) => desired,
            static (left, right) => !Equals(left, right),
            resolveRebaseUpstream: null,
            applyRebaseUpstream: null,
            Clone,
            value,
            hasDefaultValue: false,
            upstreamState: null,
            upstreamGeneration: null
        ) { }

    /// <summary>Creates a configure session with independent loaded and default baselines.</summary>
    public EditSession(
        T value,
        Func<T, CancellationToken, ValueTask<StateWriteReceipt>> save,
        Func<T, T> clone,
        T loadedValue,
        T defaultValue
    )
        : this(
            value,
            new StateSnapshot<T>(loadedValue, null),
            AsCommitSave(save),
            static (_, desired, _) => desired,
            static (left, right) => !Equals(left, right),
            resolveRebaseUpstream: null,
            applyRebaseUpstream: null,
            clone,
            defaultValue,
            hasDefaultValue: true,
            upstreamState: null,
            upstreamGeneration: null
        ) { }

    /// <summary>Creates a snapshot-backed configure session with upstream rebasing support.</summary>
    /// <param name="value">The initial editable draft.</param>
    /// <param name="sessionStart">The snapshot captured when the session was opened.</param>
    /// <param name="save">Saves one selected value to the backing state and returns the effective committed state.</param>
    /// <param name="rebase">Rebases draft changes as (baseline, desired, current) onto the current upstream value.</param>
    /// <param name="hasChanges">Reports whether a draft differs from a baseline.</param>
    /// <param name="resolveUpstream">Resolves the latest upstream snapshot; also reports upstream changes.</param>
    /// <param name="clone">Clones model values owned by this session.</param>
    /// <param name="defaultValue">The model-default value.</param>
    /// <param name="upstreamState">The state view used to subscribe for upstream change notifications.</param>
    public EditSession(
        T value,
        StateSnapshot<T> sessionStart,
        Func<T, CancellationToken, ValueTask<StateCommitResult<T>>> save,
        Func<T, T, T, T> rebase,
        Func<T, T, bool> hasChanges,
        Func<CancellationToken, ValueTask<StateSnapshot<T>>> resolveUpstream,
        Func<T, T> clone,
        T defaultValue,
        IReadOnlyState<T>? upstreamState
    )
        : this(
            value,
            sessionStart,
            save,
            rebase,
            hasChanges,
            WrapResolveUpstream(resolveUpstream),
            applyRebaseUpstream: null,
            clone,
            defaultValue,
            hasDefaultValue: true,
            upstreamState,
            upstreamGeneration: null
        ) { }

    internal EditSession(
        T value,
        StateSnapshot<T> sessionStart,
        Func<T, CancellationToken, ValueTask<StateCommitResult<T>>> save,
        Func<T, T, T, T> rebase,
        Func<T, T, bool> hasChanges,
        Func<CancellationToken, ValueTask<SessionUpstreamResolution<T>>> resolveRebaseUpstream,
        Func<T, T> clone,
        T defaultValue,
        IReadOnlyState<T>? upstreamState,
        UpstreamGenerationCounter upstreamGeneration
    )
        : this(
            value,
            sessionStart,
            save,
            rebase,
            hasChanges,
            resolveRebaseUpstream,
            applyRebaseUpstream: null,
            clone,
            defaultValue,
            hasDefaultValue: true,
            upstreamState,
            upstreamGeneration
        ) { }

    internal EditSession(
        T value,
        StateSnapshot<T> sessionStart,
        Func<T, CancellationToken, ValueTask<StateCommitResult<T>>> save,
        Func<T, T, T, T> rebase,
        Func<T, T, bool> hasChanges,
        Func<CancellationToken, ValueTask<SessionUpstreamResolution<T>>>? resolveRebaseUpstream,
        Action<SessionUpstreamResolution<T>>? applyRebaseUpstream,
        Func<T, T> clone,
        T defaultValue,
        IReadOnlyState<T>? upstreamState,
        UpstreamGenerationCounter? upstreamGeneration
    )
        : this(
            value,
            sessionStart,
            save,
            rebase,
            hasChanges,
            resolveRebaseUpstream,
            applyRebaseUpstream,
            clone,
            defaultValue,
            hasDefaultValue: true,
            upstreamState,
            upstreamGeneration
        ) { }

    private EditSession(
        T value,
        StateSnapshot<T> sessionStart,
        Func<T, CancellationToken, ValueTask<StateCommitResult<T>>> save,
        Func<T, T, T, T> rebase,
        Func<T, T, bool> hasChanges,
        Func<CancellationToken, ValueTask<SessionUpstreamResolution<T>>>? resolveRebaseUpstream,
        Action<SessionUpstreamResolution<T>>? applyRebaseUpstream,
        Func<T, T> clone,
        T defaultValue,
        bool hasDefaultValue,
        IReadOnlyState<T>? upstreamState,
        UpstreamGenerationCounter? upstreamGeneration
    )
    {
        ArgumentNullException.ThrowIfNull(sessionStart);
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _rebase = rebase ?? throw new ArgumentNullException(nameof(rebase));
        _hasChanges = hasChanges ?? throw new ArgumentNullException(nameof(hasChanges));
        _clone = clone ?? throw new ArgumentNullException(nameof(clone));
        _resolveRebaseUpstream = resolveRebaseUpstream;
        _applyRebaseUpstream = applyRebaseUpstream;
        _sessionStart = sessionStart;
        _sessionStartValue = _clone(sessionStart.Value);
        _hasSessionStartValue =
            typeof(T).IsValueType
            || typeof(T) == typeof(string)
            || !ReferenceEquals(sessionStart.Value, _sessionStartValue);
        _defaultValue = _clone(defaultValue);
        _hasDefaultValue = hasDefaultValue;
        _upstreamGeneration = upstreamGeneration ?? new UpstreamGenerationCounter();
        _baseline = _clone(sessionStart.Value);
        Value = _clone(value);
        if (upstreamState is not null)
        {
            _latestUpstream = sessionStart;
            _upstreamSubscription = upstreamState.OnChange(OnUpstreamChanged);
        }
    }

    /// <summary>Raised after an upstream change notification is observed.</summary>
    public event Action? UpstreamChanged;

    /// <summary>The editable model value for this session.</summary>
    public T Value { get; set; }

    /// <summary>The editable model value for this session.</summary>
    public T CurrentValue => Value;

    /// <summary>The snapshot captured when this session was opened.</summary>
    public StateSnapshot<T> SessionStart => _sessionStart;

    /// <summary>The latest upstream snapshot known to this session; null when no upstream is observed.</summary>
    public StateSnapshot<T>? LatestUpstream
    {
        get
        {
            lock (_upstreamGate)
            {
                return _latestUpstream;
            }
        }
    }

    /// <summary>Whether the session has been saved successfully.</summary>
    public bool IsCommitted => Volatile.Read(ref _isCommitted) != 0;

    internal Func<CancellationToken, ValueTask>? AfterSaveResolved { get; set; }

    /// <summary>Whether the draft differs from the current baseline.</summary>
    public bool HasLocalChanges
    {
        get
        {
            lock (_upstreamGate)
            {
                return _hasChanges(Value, _baseline);
            }
        }
    }

    /// <summary>Whether upstream changed since the current baseline.</summary>
    public bool HasUpstreamChanges
    {
        get
        {
            lock (_upstreamGate)
            {
                return _hasUpstreamChanges;
            }
        }
    }

    /// <summary>Updates the editable model value.</summary>
    public void Update(Action<T> updater)
    {
        ArgumentNullException.ThrowIfNull(updater);
        EnsureEditable();
        updater(Value);
    }

    /// <summary>Restores the value that was loaded when this session was opened.</summary>
    public void ResetToSessionStart()
    {
        EnsureEditable();
        EnsureSessionStartValue();
        var value = _clone(_sessionStartValue);
        lock (_upstreamGate)
        {
            Value = value;
            _baseline = _sessionStartValue;
            _hasUpstreamChanges = false;
        }
    }

    /// <summary>Discards local draft changes in favor of the latest known upstream snapshot.</summary>
    /// <remarks>Performs no I/O and throws when no upstream snapshot has ever been observed.</remarks>
    public void ResetToUpstream()
    {
        EnsureEditable();
        StateSnapshot<T> upstream;
        lock (_upstreamGate)
        {
            upstream =
                _latestUpstream
                ?? throw new InvalidOperationException(
                    "This configure session has no upstream snapshot to reset to."
                );
        }

        var value = _clone(upstream.Value);
        lock (_upstreamGate)
        {
            Value = value;
            _baseline = upstream.Value;
            _hasUpstreamChanges = false;
        }
    }

    /// <summary>Resets the editable value to the model's default value.</summary>
    public void ResetToDefault()
    {
        EnsureEditable();
        EnsureDefaultValue();
        Value = _clone(_defaultValue);
    }

    /// <summary>Resets selected parts of the editable value to the model's default value.</summary>
    public void ResetToDefault(Action<T, T> reset)
    {
        ArgumentNullException.ThrowIfNull(reset);
        EnsureEditable();
        EnsureDefaultValue();
        reset(Value, _clone(_defaultValue));
    }

    /// <summary>Resolves the latest upstream snapshot and reapplies the local draft changes onto it.</summary>
    /// <remarks>Holds the session operation state across the upstream read, rebase
    /// computation, and result publication, so a concurrent <see cref="CommitAsync"/>
    /// or <see cref="RebaseAsync"/> is rejected instead of overwriting its result.</remarks>
    public async ValueTask RebaseAsync(CancellationToken cancellationToken = default)
    {
        EnsureEditable();
        if (_resolveRebaseUpstream is null)
        {
            throw new InvalidOperationException(
                "This configure session does not support upstream rebasing."
            );
        }

        if (Interlocked.CompareExchange(ref _state, OperationState, 0) != 0)
        {
            throw new InvalidOperationException(
                "This configure session is already saving, rebasing, or disposed."
            );
        }

        try
        {
            var resolved = await _resolveRebaseUpstream(cancellationToken).ConfigureAwait(false);
            var upstream = resolved.Snapshot;
            T baseline;
            T draft;
            lock (_upstreamGate)
            {
                baseline = _baseline;
                draft = Value;
            }

            // May throw (conflict/cancel): neither the public baseline nor the saved
            // baseline may advance in that case.
            var rebased = _rebase(baseline, draft, upstream.Value);
            // Clone before mutating so a clone failure also leaves both baselines unchanged.
            var rebasedClone = _clone(rebased);
            var baselineClone = _clone(upstream.Value);
            lock (_upstreamGate)
            {
                Value = rebasedClone;
                _baseline = baselineClone;
                _latestUpstream = upstream;
                _hasUpstreamChanges = false;
            }

            // Only after the rebase computation and clones succeeded do we advance the
            // saved baseline/revisions to the same snapshot.
            _applyRebaseUpstream?.Invoke(resolved);
        }
        finally
        {
            CompleteOperation();
        }
    }

    /// <summary>Commits the edited value against the latest resolved source state.</summary>
    public async ValueTask<StateWriteReceipt> CommitAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (Interlocked.CompareExchange(ref _state, OperationState, 0) != 0)
        {
            throw new InvalidOperationException(
                "This configure session is already saving, rebasing, or disposed."
            );
        }

        try
        {
            long observedGeneration;
            lock (_upstreamGate)
            {
                observedGeneration = _upstreamGeneration.Capture();
            }

            var result = await _save(_clone(Value), cancellationToken).ConfigureAwait(false);
            if (AfterSaveResolved is { } afterSaveResolved)
            {
                await afterSaveResolved(cancellationToken).ConfigureAwait(false);
            }

            Volatile.Write(ref _isCommitted, 1);
            lock (_upstreamGate)
            {
                var committedValue = result.CommittedSnapshot.Value;
                Value = _clone(committedValue);
                _baseline = _clone(committedValue);

                var resultGeneration = result.UpstreamGeneration ?? observedGeneration;
                if (_upstreamGeneration.Capture() == resultGeneration)
                {
                    _latestUpstream = result.CommittedSnapshot;
                    _hasUpstreamChanges = false;
                }
                else
                {
                    _hasUpstreamChanges = true;
                }
            }

            CompleteOperation();
            return result.Receipt;
        }
        catch
        {
            CompleteOperation();
            throw;
        }
    }

    /// <summary>Discards this session without saving it.</summary>
    /// <remarks>An operation (save or rebase) already in progress is allowed to finish, then the session becomes disposed.</remarks>
    public void Dispose()
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if ((state & DisposedState) != 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _state, state | DisposedState, state) == state)
            {
                Interlocked.Exchange(ref _upstreamSubscription, null)?.Dispose();
                return;
            }
        }
    }

    private void OnUpstreamChanged(T value)
    {
        lock (_upstreamGate)
        {
            _upstreamGeneration.Advance();
            _latestUpstream = new StateSnapshot<T>(value, null);
            _hasUpstreamChanges = true;
        }

        UpstreamChanged?.Invoke();
    }

    private void EnsureEditable()
    {
        if (Volatile.Read(ref _state) != 0)
        {
            throw new InvalidOperationException(
                "This configure session is already saving, rebasing, or disposed."
            );
        }
    }

    private void EnsureDefaultValue()
    {
        if (!_hasDefaultValue)
        {
            throw new InvalidOperationException(
                "This configure session was not given a model-default baseline."
            );
        }
    }

    private void EnsureSessionStartValue()
    {
        if (!_hasSessionStartValue)
        {
            throw new InvalidOperationException(
                "This configure session's session-start baseline could not be cloned."
            );
        }
    }

    private void CompleteOperation()
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            var completedState = state & ~OperationState;
            if (Interlocked.CompareExchange(ref _state, completedState, state) == state)
            {
                return;
            }
        }
    }

    private static T Clone(T value) =>
        value is IConfiglueDeepCloneable<T> deepCloneable ? deepCloneable.DeepClone() : value;

    private static Func<
        CancellationToken,
        ValueTask<SessionUpstreamResolution<T>>
    >? WrapResolveUpstream(Func<CancellationToken, ValueTask<StateSnapshot<T>>>? resolveUpstream)
    {
        if (resolveUpstream is null)
        {
            return null;
        }

        return async cancellationToken =>
        {
            var snapshot = await resolveUpstream(cancellationToken).ConfigureAwait(false);
            return new SessionUpstreamResolution<T>(snapshot, Revisions: null);
        };
    }

    private static Func<T, CancellationToken, ValueTask<StateCommitResult<T>>> AsCommitSave(
        Func<T, CancellationToken, ValueTask<StateWriteReceipt>> save
    )
    {
        ArgumentNullException.ThrowIfNull(save);
        return async (value, cancellationToken) =>
        {
            var receipt = await save(value, cancellationToken).ConfigureAwait(false);
            return new StateCommitResult<T>(receipt, new StateSnapshot<T>(value, null));
        };
    }
}
