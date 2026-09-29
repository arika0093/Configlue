namespace Configlue;

/// <summary>Scoped facade that resolves the accessor's current subject for each operation.</summary>
internal sealed class CurrentSubjectOptions<TModel>(
    ISubjectOptions<TModel> subjectOptions,
    IConfiglueSubjectAccessor subjectAccessor
) : IWritableOptions<TModel>
{
    public async ValueTask<TModel> GetValueAsync(CancellationToken cancellationToken = default)
    {
        var subject = await subjectAccessor
            .GetCurrentSubjectAsync(cancellationToken)
            .ConfigureAwait(false);
        return await subjectOptions
            .For(subject)
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
            .For(subject)
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
}

/// <summary>Rebinds one options watcher after an accessor reports context invalidation.</summary>
internal sealed class SubjectChangeSubscription<TModel> : IDisposable
{
    private readonly ISubjectOptions<TModel> _subjectOptions;
    private readonly IConfiglueSubjectAccessor _subjectAccessor;
    private readonly Action<TModel> _listener;
    private readonly IConfiglueSubjectChangeSource? _changeSource;
    private readonly SemaphoreSlim _rebindLock = new(1, 1);
    private readonly CancellationTokenSource _cancellation = new();
    private IDisposable? _subjectSubscription;
    private IDisposable? _invalidationSubscription;
    private long _generation;
    private long _boundGeneration = long.MinValue;
    private int _disposed;

    public SubjectChangeSubscription(
        ISubjectOptions<TModel> subjectOptions,
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
        _ = RebindAsync();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        _cancellation.Dispose();
        Interlocked.Exchange(ref _invalidationSubscription, null)?.Dispose();
        Interlocked.Exchange(ref _subjectSubscription, null)?.Dispose();
    }

    private void OnInvalidated()
    {
        Interlocked.Increment(ref _generation);
        Interlocked.Exchange(ref _subjectSubscription, null)?.Dispose();
        _ = RebindAsync();
    }

    private async Task RebindAsync()
    {
        try
        {
            await _rebindLock.WaitAsync(_cancellation.Token).ConfigureAwait(false);
            try
            {
                while (Volatile.Read(ref _disposed) == 0)
                {
                    var generation = Volatile.Read(ref _generation);
                    if (
                        generation == Volatile.Read(ref _boundGeneration)
                        && Volatile.Read(ref _subjectSubscription) is not null
                    )
                    {
                        break;
                    }

                    var subject = await _subjectAccessor
                        .GetCurrentSubjectAsync(_cancellation.Token)
                        .ConfigureAwait(false);
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        break;
                    }

                    var subjectOptions = _subjectOptions.For(subject);
                    var next = subjectOptions.OnChange(value =>
                    {
                        if (
                            Volatile.Read(ref _disposed) == 0
                            && generation == Volatile.Read(ref _generation)
                        )
                        {
                            _listener(value);
                        }
                    });
                    if (
                        Volatile.Read(ref _disposed) != 0
                        || generation != Volatile.Read(ref _generation)
                    )
                    {
                        next.Dispose();
                        if (Volatile.Read(ref _disposed) != 0)
                        {
                            break;
                        }

                        continue;
                    }

                    Interlocked.Exchange(ref _subjectSubscription, next)?.Dispose();
                    if (
                        Volatile.Read(ref _disposed) != 0
                        || generation != Volatile.Read(ref _generation)
                    )
                    {
                        Interlocked.Exchange(ref _subjectSubscription, null)?.Dispose();
                        if (Volatile.Read(ref _disposed) != 0)
                        {
                            break;
                        }

                        continue;
                    }

                    if (generation > 0)
                    {
                        var value = await subjectOptions
                            .GetValueAsync(_cancellation.Token)
                            .ConfigureAwait(false);
                        if (
                            Volatile.Read(ref _disposed) == 0
                            && generation == Volatile.Read(ref _generation)
                        )
                        {
                            Volatile.Write(ref _boundGeneration, generation);
                            _listener(value);
                        }
                    }
                    else
                    {
                        Volatile.Write(ref _boundGeneration, generation);
                    }

                    break;
                }
            }
            finally
            {
                _rebindLock.Release();
            }
        }
        catch (Exception exception)
        {
            if (
                Volatile.Read(ref _disposed) == 0
                && exception is not OperationCanceledException
                && exception is not ObjectDisposedException
            )
            {
                // The sync subscription contract has no error channel. A later invalidation retries;
                // reads and writes surface accessor failures directly to their caller.
                System.Diagnostics.Trace.TraceError(
                    "Configlue could not bind a subject-specific change watcher: {0}",
                    exception
                );
            }
        }
    }
}
