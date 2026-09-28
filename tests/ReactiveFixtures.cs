namespace Configlue.Tests.ReactiveSupport;

internal sealed class FakeOptions<T>(T value) : IWritableOptions<T>, IConfiglueDiagnostics<T>
{
    private event Action<T>? Changed;
    private event Action<Exception>? Failed;
    public T Value { get; set; } = value;
    public TaskCompletionSource<T>? PendingRead { get; set; }
    public Exception? ReadError { get; set; }
    public Action<Action<T>>? Attaching { get; set; }
    public CancellationToken ReadToken { get; private set; }
    public int ValueListeners { get; private set; }
    public int FailureListeners { get; private set; }
    public int ReadCount { get; private set; }

    public IDisposable OnChange(Action<T> listener)
    {
        Changed += listener;
        ValueListeners++;
        Attaching?.Invoke(listener);
        return new CallbackDisposable(() =>
        {
            Changed -= listener;
            ValueListeners--;
        });
    }

    public ValueTask<T> GetValueAsync(CancellationToken cancellationToken = default)
    {
        ReadCount++;
        ReadToken = cancellationToken;
        if (ReadError is not null)
        {
            return ValueTask.FromException<T>(ReadError);
        }
        return PendingRead is null ? ValueTask.FromResult(Value) : new(PendingRead.Task);
    }

    public IDisposable OnReloadFailed(Action<Exception> listener)
    {
        Failed += listener;
        FailureListeners++;
        return new CallbackDisposable(() =>
        {
            Failed -= listener;
            FailureListeners--;
        });
    }

    public ValueTask<StateWriteReceipt> SaveAsync(
        IConfigluePatch patch,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException();

    public ConfiglueOptionsDiagnostics GetDiagnostics() => throw new NotSupportedException();

    public void Emit(T value)
    {
        Value = value;
        Changed?.Invoke(value);
    }

    public void Fail(Exception exception) => Failed?.Invoke(exception);
}

internal sealed class FakeProfiles : IConfiglueProfiledOptions<int>
{
    private event Action<int>? Changed;
    private event Action<string>? ProfileChanged;
    public Dictionary<string, FakeOptions<int>> ProfileOptions { get; } =
        new(StringComparer.Ordinal) { ["default"] = new(1), ["other"] = new(2) };
    public Dictionary<
        string,
        TaskCompletionSource<IWritableOptions<int>>
    > PendingProfiles { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, CancellationToken> ProfileReadTokens { get; } =
        new(StringComparer.Ordinal);
    public string Name { get; set; } = "default";
    public bool DeferValueListenerBinding { get; set; }
    public TaskCompletionSource<int>? PendingValue
    {
        get => ProfileOptions["default"].PendingRead;
        set => ProfileOptions["default"].PendingRead = value;
    }
    public TaskCompletionSource<string>? PendingName { get; set; }
    public int ManagerValueListeners { get; private set; }
    public int ValueListeners =>
        ManagerValueListeners + ProfileOptions.Values.Sum(static options => options.ValueListeners);
    public int NameListeners { get; private set; }
    public string DefaultProfileName => "default";
    public event Action<string>? ActiveProfileChanged
    {
        add
        {
            ProfileChanged += value;
            NameListeners++;
        }
        remove
        {
            ProfileChanged -= value;
            NameListeners--;
        }
    }

    public IDisposable OnChange(Action<int> listener)
    {
        // Model asynchronous profile-manager binding: the value listener may not yet exist.
        if (!DeferValueListenerBinding)
        {
            Changed += listener;
        }
        ManagerValueListeners++;
        return new CallbackDisposable(() =>
        {
            Changed -= listener;
            ManagerValueListeners--;
        });
    }

    public ValueTask<int> GetActiveValueAsync(CancellationToken cancellationToken = default) =>
        ProfileOptions[Name].GetValueAsync(cancellationToken);

    public ValueTask<string> GetActiveProfileNameAsync(
        CancellationToken cancellationToken = default
    ) => PendingName is null ? ValueTask.FromResult(Name) : new(PendingName.Task);

    public ValueTask SetActiveProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default
    )
    {
        Name = profileName;
        ProfileChanged?.Invoke(Name);
        Changed?.Invoke(ProfileOptions[Name].Value);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyCollection<string>> GetProfileNamesAsync(
        CancellationToken cancellationToken = default
    ) => ValueTask.FromResult<IReadOnlyCollection<string>>(ProfileOptions.Keys);

    public ValueTask<IWritableOptions<int>> GetProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default
    )
    {
        ProfileReadTokens[profileName] = cancellationToken;
        return PendingProfiles.TryGetValue(profileName, out var pending)
            ? new(pending.Task)
            : ValueTask.FromResult<IWritableOptions<int>>(ProfileOptions[profileName]);
    }

    public ValueTask<IWritableOptions<int>> GetActiveProfileAsync(
        CancellationToken cancellationToken = default
    ) => GetProfileAsync(Name, cancellationToken);

    public ValueTask CreateProfileAsync(
        string profileName,
        string? copyFrom = null,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException();

    public ValueTask RemoveProfileAsync(
        string profileName,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException();
}

internal sealed class ManualContext : SynchronizationContext
{
    private readonly Queue<Action> _work = new();

    public override void Post(SendOrPostCallback callback, object? state) =>
        _work.Enqueue(() => callback(state));

    public void Run()
    {
        while (_work.TryDequeue(out var action))
        {
            action();
        }
    }
}

internal sealed class CallbackDisposable(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;

    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}
