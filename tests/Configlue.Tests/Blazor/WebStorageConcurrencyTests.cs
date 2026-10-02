using System.Text;
using Configlue.Hosting.Blazor;
using Configlue.Resources;
using Configlue.State;
using Microsoft.JSInterop;

namespace Configlue.Tests;

public sealed class WebStorageConcurrencyTests
{
    [Test]
    public async Task MatchRace_CommitsExactlyOneWriter()
    {
        var store = new BrowserStore();
        var writerA = CreateWriter(store);
        var writerB = CreateWriter(store);
        var observed = (
            await writerA.WriteAsync(new ResourceWriteRequest(Bytes("initial")))
        ).Revision;
        observed.ShouldNotBeNullOrEmpty();

        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.PauseBeforeCommit = _ =>
        {
            paused.TrySetResult();
            return resume.Task;
        };

        var first = writerA
            .WriteAsync(new ResourceWriteRequest(Bytes("a"), RevisionCondition.Match(observed!)))
            .AsTask();
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondWaiting = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        store.AcquireWaitStarted = () => secondWaiting.TrySetResult();
        var second = writerB
            .WriteAsync(new ResourceWriteRequest(Bytes("b"), RevisionCondition.Match(observed!)))
            .AsTask();
        await secondWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        second.IsCompleted.ShouldBeFalse(
            "The losing writer must wait for the browser-side lock instead of racing the compare."
        );

        resume.TrySetResult();
        (await first).Revision.ShouldNotBeNullOrEmpty();
        await Should.ThrowAsync<StateConflictException>(async () => await second);
        (await writerA.ReadAsync()).Content.ToArray().ShouldBe(Bytes("a"));
    }

    [Test]
    public async Task MustNotExistRace_CommitsExactlyOneWriter()
    {
        var store = new BrowserStore();
        var writerA = CreateWriter(store);
        var writerB = CreateWriter(store);

        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.PauseBeforeCommit = _ =>
        {
            paused.TrySetResult();
            return resume.Task;
        };

        var first = writerA
            .WriteAsync(new ResourceWriteRequest(Bytes("a"), RevisionCondition.MustNotExist))
            .AsTask();
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondWaiting = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        store.AcquireWaitStarted = () => secondWaiting.TrySetResult();
        var second = writerB
            .WriteAsync(new ResourceWriteRequest(Bytes("b"), RevisionCondition.MustNotExist))
            .AsTask();
        await secondWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        second.IsCompleted.ShouldBeFalse();

        resume.TrySetResult();
        (await first).Revision.ShouldNotBeNullOrEmpty();
        await Should.ThrowAsync<StateConflictException>(async () => await second);
        (await writerA.ReadAsync()).Content.ToArray().ShouldBe(Bytes("a"));
    }

    [Test]
    public async Task IndependentCircuitRuntimes_ShareOneBrowserStoreAndDoNotBypassConcurrency()
    {
        var store = new BrowserStore();
        var runtimeA = new BrowserStoreJsRuntime(store);
        var runtimeB = new BrowserStoreJsRuntime(store);
        ReferenceEquals(runtimeA, runtimeB).ShouldBeFalse();
        var writerA = new WebStorageResource(runtimeA, WebStorageKind.Session, "circuit");
        var writerB = new WebStorageResource(runtimeB, WebStorageKind.Session, "circuit");

        var observed = (
            await writerA.WriteAsync(new ResourceWriteRequest(Bytes("start")))
        ).Revision;
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.PauseBeforeCommit = _ =>
        {
            paused.TrySetResult();
            return resume.Task;
        };

        var first = writerA
            .WriteAsync(new ResourceWriteRequest(Bytes("a"), RevisionCondition.Match(observed!)))
            .AsTask();
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondWaiting = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        store.AcquireWaitStarted = () => secondWaiting.TrySetResult();
        var second = writerB
            .WriteAsync(new ResourceWriteRequest(Bytes("b"), RevisionCondition.Match(observed!)))
            .AsTask();
        await secondWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        resume.TrySetResult();
        (await first).Revision.ShouldNotBeNullOrEmpty();
        await Should.ThrowAsync<StateConflictException>(async () => await second);
    }

    [Test]
    public async Task UnconditionalWrites_KeepDocumentedLastWriterWinsSemantics()
    {
        var store = new BrowserStore();
        var writer = CreateWriter(store);

        var first = await writer.WriteAsync(new ResourceWriteRequest(Bytes("one")));
        var second = await writer.WriteAsync(new ResourceWriteRequest(Bytes("two")));

        first.Revision.ShouldNotBe(second.Revision);
        (await writer.ReadAsync()).Content.ToArray().ShouldBe(Bytes("two"));
        store.AcquireCount.ShouldBe(
            0,
            "Unconditional writes must not participate in the conditional lock protocol."
        );
    }

    [Test]
    public async Task UnavailableJavascript_IsClassifiedSeparatelyFromConflicts()
    {
        var store = new BrowserStore();
        var writer = CreateWriter(store);
        store.JavascriptAvailable = false;

        (await writer.ReadAsync()).Status.ShouldBe(StateReadStatus.Unavailable);
        await Should.ThrowAsync<WebStorageUnavailableException>(async () =>
            await writer.WriteAsync(
                new ResourceWriteRequest(Bytes("x"), RevisionCondition.MustNotExist)
            )
        );
        await Should.ThrowAsync<WebStorageUnavailableException>(async () =>
            await writer.WriteAsync(new ResourceWriteRequest(Bytes("x")))
        );
    }

    [Test]
    public async Task MissingWebLocks_RejectsConditionalWritesWithoutEmulatingCompareAndSwap()
    {
        var store = new BrowserStore { WebLocksAvailable = false };
        var writer = CreateWriter(store);

        await Should.ThrowAsync<WebStorageAtomicityNotSupportedException>(async () =>
            await writer.WriteAsync(
                new ResourceWriteRequest(Bytes("x"), RevisionCondition.MustNotExist)
            )
        );
        (await writer.ReadAsync()).Status.ShouldBe(StateReadStatus.NotFound);

        var committed = await writer.WriteAsync(new ResourceWriteRequest(Bytes("y")));
        committed.Revision.ShouldNotBeNullOrEmpty();
        (await writer.ReadAsync()).Content.ToArray().ShouldBe(Bytes("y"));
    }

    [Test]
    public async Task MissingHelperScript_IsClassifiedAsAtomicityNotSupported()
    {
        var store = new BrowserStore { HelperAvailable = false };
        var writer = CreateWriter(store);

        await Should.ThrowAsync<WebStorageAtomicityNotSupportedException>(async () =>
            await writer.WriteAsync(
                new ResourceWriteRequest(Bytes("x"), RevisionCondition.MustNotExist)
            )
        );

        var committed = await writer.WriteAsync(new ResourceWriteRequest(Bytes("y")));
        committed.Revision.ShouldNotBeNullOrEmpty();
        (await writer.ReadAsync()).Content.ToArray().ShouldBe(Bytes("y"));
    }

    [Test]
    public async Task ReleaseFailure_DoesNotFailACommittedConditionalWrite()
    {
        var store = new BrowserStore { FailRelease = true };
        var writer = CreateWriter(store);

        var result = await writer.WriteAsync(
            new ResourceWriteRequest(Bytes("v"), RevisionCondition.MustNotExist)
        );

        result.Revision.ShouldNotBeNullOrEmpty();
        (await writer.ReadAsync()).Content.ToArray().ShouldBe(Bytes("v"));
    }

    private static WebStorageResource CreateWriter(BrowserStore store) =>
        new(new BrowserStoreJsRuntime(store), WebStorageKind.Local, "shared-key");

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private sealed class BrowserStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
        private readonly object _sync = new();

        public bool JavascriptAvailable { get; set; } = true;

        public bool HelperAvailable { get; set; } = true;

        public bool WebLocksAvailable { get; set; } = true;

        public bool FailRelease { get; set; }

        public int AcquireCount { get; private set; }

        public Func<string, Task>? PauseBeforeCommit { get; set; }

        public Action? AcquireWaitStarted { get; set; }

        public string? GetItem(string key)
        {
            EnsureAvailable();
            lock (_sync)
            {
                return _values.TryGetValue(key, out var value) ? value : null;
            }
        }

        public async Task SetItemAsync(string key, string value)
        {
            EnsureAvailable();
            if (PauseBeforeCommit is { } pause)
            {
                await pause(key).ConfigureAwait(false);
            }

            lock (_sync)
            {
                _values[key] = value;
            }
        }

        public async Task<string> AcquireAsync(string storageName, string key)
        {
            EnsureAvailable();
            if (!HelperAvailable)
            {
                throw new JSException("configlueWebStorage is not defined.");
            }

            if (!WebLocksAvailable)
            {
                return "unsupported";
            }

            AcquireCount++;
            AcquireWaitStarted?.Invoke();
            await Gate(storageName + "\u0000" + key).WaitAsync().ConfigureAwait(false);
            return "web-locks";
        }

        public void Release(string storageName, string key)
        {
            if (!WebLocksAvailable)
            {
                return;
            }

            if (FailRelease)
            {
                throw new JSException("release failed");
            }

            Gate(storageName + "\u0000" + key).Release();
        }

        private SemaphoreSlim Gate(string name)
        {
            lock (_sync)
            {
                if (!_gates.TryGetValue(name, out var gate))
                {
                    gate = new SemaphoreSlim(1, 1);
                    _gates.Add(name, gate);
                }

                return gate;
            }
        }

        private void EnsureAvailable()
        {
            if (!JavascriptAvailable)
            {
                throw new InvalidOperationException("JavaScript is unavailable.");
            }
        }
    }

    private sealed class BrowserStoreJsRuntime : IJSRuntime
    {
        private readonly BrowserStore _store;

        public BrowserStoreJsRuntime(BrowserStore store) => _store = store;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, default, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args
        )
        {
            if (string.Equals(identifier, "configlueWebStorage.acquire", StringComparison.Ordinal))
            {
                var mode = await _store
                    .AcquireAsync((string)args![0]!, (string)args[1]!)
                    .ConfigureAwait(false);
                return (TValue)(object)mode;
            }

            if (string.Equals(identifier, "configlueWebStorage.release", StringComparison.Ordinal))
            {
                _store.Release((string)args![0]!, (string)args[1]!);
                return default!;
            }

            if (identifier.EndsWith(".getItem", StringComparison.Ordinal))
            {
                return (TValue)(object?)_store.GetItem((string)args![0]!)!;
            }

            if (identifier.EndsWith(".setItem", StringComparison.Ordinal))
            {
                await _store
                    .SetItemAsync((string)args![0]!, (string)args[1]!)
                    .ConfigureAwait(false);
                return default!;
            }

            throw new NotSupportedException(identifier);
        }
    }
}
