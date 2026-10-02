using Configlue.CompilerServices;

namespace Configlue;

/// <summary>Scoped facade that resolves the accessor's current subject for each operation.</summary>
internal sealed class CurrentSubjectState<TModel>(
    ISubjectState<TModel> subjectOptions,
    IConfiglueSubjectAccessor subjectAccessor
)
    : IWritableState<TModel>,
        IConfiglueDetailsRuntime,
        IConfiglueStateSnapshotRuntime<TModel>,
        IConfiglueInspection<TModel>,
        IConfiglueEditSessions<TModel>
{
    public ConfiglueCheckOperation Check(CancellationToken cancellationToken = default) =>
        new(
            (reportSource, token) => CheckWithCurrentSubjectAsync(reportSource, token),
            cancellationToken
        );

    private async Task<ConfiglueCheckResult> CheckWithCurrentSubjectAsync(
        Action<ConfiglueSourceCheckResult> reportSource,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(reportSource);
        var subject = await subjectAccessor
            .GetCurrentSubjectAsync(cancellationToken)
            .ConfigureAwait(false);
        var options = subjectOptions.ForSubject(subject);
        if (options is not IConfiglueInspection<TModel> inspection)
        {
            throw new NotSupportedException(
                "This options implementation does not expose state checks."
            );
        }

        var operation = inspection.Check(cancellationToken);
        var result = operation.Result;
        await foreach (var source in operation.ConfigureAwait(false))
        {
            reportSource(source);
        }

        return await result.ConfigureAwait(false);
    }

    public async ValueTask<TModel> GetValueAsync(CancellationToken cancellationToken = default)
    {
        var subject = await subjectAccessor
            .GetCurrentSubjectAsync(cancellationToken)
            .ConfigureAwait(false);
        return await subjectOptions
            .ForSubject(subject)
            .GetValueAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<StateWriteReceipt> SaveAsync(
        IConfigluePatch patch,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(patch);
        var subject = await subjectAccessor
            .GetCurrentSubjectAsync(cancellationToken)
            .ConfigureAwait(false);
        return await subjectOptions
            .ForSubject(subject)
            .SaveAsync(patch, cancellationToken)
            .ConfigureAwait(false);
    }

    public IDisposable OnChange(Action<TModel> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        var subscription = new SubjectChangeSubscription<TModel>(
            subjectOptions,
            subjectAccessor,
            listener
        );
        subscription.Start();
        return subscription;
    }

    async ValueTask<ConfiglueDetailsSnapshot> IConfiglueDetailsRuntime.GetDetailsSnapshotAsync(
        CancellationToken cancellationToken
    )
    {
        var subject = await subjectAccessor
            .GetCurrentSubjectAsync(cancellationToken)
            .ConfigureAwait(false);
        var options = subjectOptions.ForSubject(subject);
        if (options is not IConfiglueDetailsRuntime details)
        {
            throw new NotSupportedException(
                "This options implementation does not expose details snapshots."
            );
        }

        return await details.GetDetailsSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<StateSnapshot<TModel>> IConfiglueStateSnapshotRuntime<TModel>.GetSnapshotAsync(
        CancellationToken cancellationToken
    )
    {
        var subject = await subjectAccessor
            .GetCurrentSubjectAsync(cancellationToken)
            .ConfigureAwait(false);
        var options = subjectOptions.ForSubject(subject);
        if (options is not IConfiglueStateSnapshotRuntime<TModel> runtime)
        {
            throw new NotSupportedException(
                "This options implementation does not expose resolved snapshots."
            );
        }

        return await runtime.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<EditSession<TModel>> OpenEditSessionAsync(
        CancellationToken cancellationToken = default
    )
    {
        var sessions = await ResolveSubjectEditSessionsAsync(cancellationToken)
            .ConfigureAwait(false);
        return await sessions.OpenEditSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<EditSession<TModel>> OpenEditSessionAsync(
        StateWritePlan writePlan,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(writePlan);
        var sessions = await ResolveSubjectEditSessionsAsync(cancellationToken)
            .ConfigureAwait(false);
        return await sessions
            .OpenEditSessionAsync(writePlan, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<IConfiglueEditSessions<TModel>> ResolveSubjectEditSessionsAsync(
        CancellationToken cancellationToken
    )
    {
        var subject = await subjectAccessor
            .GetCurrentSubjectAsync(cancellationToken)
            .ConfigureAwait(false);
        var options = subjectOptions.ForSubject(subject);
        if (options is not IConfiglueEditSessions<TModel> sessions)
        {
            throw new NotSupportedException(
                "This options implementation does not expose edit sessions."
            );
        }

        return sessions;
    }
}

/// <summary>Rebinds one state watcher after an accessor reports context invalidation.</summary>
internal sealed class SubjectChangeSubscription<TModel> : IDisposable
{
    private readonly ISubjectState<TModel> _subjectOptions;
    private readonly IConfiglueSubjectAccessor _subjectAccessor;
    private readonly Action<TModel> _listener;
    private readonly IConfiglueSubjectChangeSource? _changeSource;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _invalidationSignal = new(0, 1);
    private IDisposable? _subjectSubscription;
    private IDisposable? _invalidationSubscription;
    private Task? _worker;
    private long _generation;
    private long _boundGeneration = long.MinValue;
    private int _disposed;

    public SubjectChangeSubscription(
        ISubjectState<TModel> subjectOptions,
        IConfiglueSubjectAccessor subjectAccessor,
        Action<TModel> listener
    )
    {
        _subjectOptions = subjectOptions;
        _subjectAccessor = subjectAccessor;
        _listener = listener;
        _changeSource = subjectAccessor as IConfiglueSubjectChangeSource;
    }

    public void Start()
    {
        _invalidationSubscription = _changeSource?.OnChange(OnInvalidated);
        _worker = RunAsync();
        _ = ReleaseAfterWorkerAsync();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _invalidationSubscription, null)?.Dispose();
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The worker already completed and released its cancellation source.
        }

        Interlocked.Exchange(ref _subjectSubscription, null)?.Dispose();
    }

    private void OnInvalidated()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _generation);
        try
        {
            _invalidationSignal.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposal completed between the disposed check and signaling the worker.
        }
        catch (SemaphoreFullException)
        {
            // A rebind is already pending; the worker coalesces the invalidation burst.
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                try
                {
                    await RebindOnceAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }
                catch (Exception exception)
                {
                    // The sync subscription contract has no error channel. A later invalidation retries;
                    // reads and writes surface accessor failures directly to their caller.
                    System.Diagnostics.Trace.TraceError(
                        "Configlue could not bind a subject-specific change watcher: {0}",
                        exception
                    );
                }

                await _invalidationSignal.WaitAsync(_cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // Disposal canceled the coalescing worker.
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
            // Disposal released the coalescing signal while the worker was unwinding.
        }
    }

    private async Task RebindOnceAsync()
    {
        var generation = Volatile.Read(ref _generation);
        if (
            generation == Volatile.Read(ref _boundGeneration)
            && Volatile.Read(ref _subjectSubscription) is not null
        )
        {
            return;
        }

        var subject = await _subjectAccessor
            .GetCurrentSubjectAsync(_cancellation.Token)
            .ConfigureAwait(false);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var subjectOptions = _subjectOptions.ForSubject(subject);
        var next = subjectOptions.OnChange(value =>
        {
            if (Volatile.Read(ref _disposed) == 0 && generation == Volatile.Read(ref _generation))
            {
                _listener(value);
            }
        });
        if (Volatile.Read(ref _disposed) != 0 || generation != Volatile.Read(ref _generation))
        {
            next.Dispose();
            return;
        }

        Interlocked.Exchange(ref _subjectSubscription, next)?.Dispose();
        if (Volatile.Read(ref _disposed) != 0 || generation != Volatile.Read(ref _generation))
        {
            Interlocked.Exchange(ref _subjectSubscription, null)?.Dispose();
            return;
        }

        if (generation > 0)
        {
            var value = await subjectOptions
                .GetValueAsync(_cancellation.Token)
                .ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) == 0 && generation == Volatile.Read(ref _generation))
            {
                Volatile.Write(ref _boundGeneration, generation);
                _listener(value);
            }
        }
        else
        {
            Volatile.Write(ref _boundGeneration, generation);
        }
    }

    private async Task ReleaseAfterWorkerAsync()
    {
        var worker = Volatile.Read(ref _worker);
        if (worker is not null)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Rebind failures are traced by the worker; this continuation only releases
                // the subscription's lifetime resources.
            }
        }

        _cancellation.Dispose();
        _invalidationSignal.Dispose();
    }
}
