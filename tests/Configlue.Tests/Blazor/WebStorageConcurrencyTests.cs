using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.StallInsideLock = _ =>
        {
            stalled.TrySetResult();
            return resume.Task;
        };

        var first = writerA
            .WriteAsync(new ResourceWriteRequest(Bytes("a"), RevisionCondition.Match(observed!)))
            .AsTask();
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondWaiting = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        store.WaitingForLock = () => secondWaiting.TrySetResult();
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
        store.CommittedCount.ShouldBe(1);
    }

    [Test]
    public async Task MustNotExistRace_CommitsExactlyOneWriter()
    {
        var store = new BrowserStore();
        var writerA = CreateWriter(store);
        var writerB = CreateWriter(store);

        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.StallInsideLock = _ =>
        {
            stalled.TrySetResult();
            return resume.Task;
        };

        var first = writerA
            .WriteAsync(new ResourceWriteRequest(Bytes("a"), RevisionCondition.MustNotExist))
            .AsTask();
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondWaiting = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        store.WaitingForLock = () => secondWaiting.TrySetResult();
        var second = writerB
            .WriteAsync(new ResourceWriteRequest(Bytes("b"), RevisionCondition.MustNotExist))
            .AsTask();
        await secondWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        second.IsCompleted.ShouldBeFalse();

        resume.TrySetResult();
        (await first).Revision.ShouldNotBeNullOrEmpty();
        await Should.ThrowAsync<StateConflictException>(async () => await second);
        (await writerA.ReadAsync()).Content.ToArray().ShouldBe(Bytes("a"));
        store.CommittedCount.ShouldBe(1);
    }

    [Test]
    public async Task WriterPausedBeyondWatchdogDuration_CannotBeOvertaken()
    {
        var store = new BrowserStore();
        var writerA = CreateWriter(store);
        var writerB = CreateWriter(store);
        var observed = (
            await writerA.WriteAsync(new ResourceWriteRequest(Bytes("initial")))
        ).Revision;

        // The mutation runs as one browser-side operation, so the holder may pause for an
        // arbitrarily long time (well beyond the former 30 second watchdog) and the lock is
        // still held by the callback until the mutation finishes. No timer can revoke it.
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.StallInsideLock = _ =>
        {
            stalled.TrySetResult();
            return resume.Task;
        };

        var first = writerA
            .WriteAsync(new ResourceWriteRequest(Bytes("a"), RevisionCondition.Match(observed!)))
            .AsTask();
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondWaiting = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        store.WaitingForLock = () => secondWaiting.TrySetResult();
        var second = writerB
            .WriteAsync(new ResourceWriteRequest(Bytes("b"), RevisionCondition.Match(observed!)))
            .AsTask();
        await secondWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        store.ElapsedLogicalTime += TimeSpan.FromSeconds(31);
        second.IsCompleted.ShouldBeFalse(
            "Time passing beyond the former watchdog must not release the lock while its holder can still commit."
        );

        resume.TrySetResult();
        (await first).Revision.ShouldNotBeNullOrEmpty();
        await Should.ThrowAsync<StateConflictException>(async () => await second);
        (await writerA.ReadAsync()).Content.ToArray().ShouldBe(Bytes("a"));
        store.CommittedCount.ShouldBe(1);
    }

    [Test]
    public async Task ConditionalWrite_IsOneAtomicMutationWithoutATimeout()
    {
        var store = new BrowserStore();
        var writer = CreateWriter(store);
        var observed = (
            await writer.WriteAsync(new ResourceWriteRequest(Bytes("initial")))
        ).Revision;
        store.ResetCounters();

        await writer.WriteAsync(
            new ResourceWriteRequest(Bytes("next"), RevisionCondition.Match(observed!))
        );

        store.MutateCount.ShouldBe(1);
        store.LastMutateArgumentCount.ShouldBe(
            5,
            "The atomic helper takes no watchdog timeout argument."
        );
        store.SetItemCount.ShouldBe(
            0,
            "The commit must happen inside the atomic helper, not through a separate interop call."
        );
    }

    [Test]
    public async Task ConditionalWriteAgainstRawValue_MatchesTheObservedContentHash()
    {
        var store = new BrowserStore();
        var writer = CreateWriter(store);
        store.SetRaw("shared-key", "written-outside-configlue");
        var observed = (await writer.ReadAsync()).Revision;
        observed.ShouldNotBeNullOrEmpty();

        await Should.ThrowAsync<StateConflictException>(async () =>
            await writer.WriteAsync(
                new ResourceWriteRequest(Bytes("x"), RevisionCondition.Match("stale"))
            )
        );

        var committed = await writer.WriteAsync(
            new ResourceWriteRequest(Bytes("x"), RevisionCondition.Match(observed!))
        );
        committed.Revision.ShouldNotBeNullOrEmpty();
        (await writer.ReadAsync()).Content.ToArray().ShouldBe(Bytes("x"));
    }

    [Test]
    [Arguments("{\"content\":\"not-base64\",\"revision\":\"legacy\"}")]
    [Arguments("{\"content\":\"YQ==\",\"revision\":123}")]
    [Arguments("{\"Content\":\"YQ==\",\"Revision\":\"legacy\"}")]
    public async Task ConditionalWriteAgainstExternalEnvelope_MatchesReaderRevision(string raw)
    {
        var store = new BrowserStore();
        store.SetRaw("shared-key", raw);
        var writer = CreateWriter(store);
        var read = await writer.ReadAsync();
        await writer.WriteAsync(
            new ResourceWriteRequest(Bytes("updated"), RevisionCondition.Match(read.Revision!))
        );
        (await writer.ReadAsync()).Content.ToArray().ShouldBe(Bytes("updated"));
    }

    [Test]
    public async Task EmptyStoredValue_IsMissingForConditionalWrite()
    {
        var store = new BrowserStore();
        store.SetRaw("shared-key", "");
        var writer = CreateWriter(store);
        (await writer.ReadAsync()).Status.ShouldBe(StateReadStatus.NotFound);
        await writer.WriteAsync(
            new ResourceWriteRequest(Bytes("new"), RevisionCondition.MustNotExist)
        );
        (await writer.ReadAsync()).Status.ShouldBe(StateReadStatus.Success);
    }

    [Test]
    public async Task AbandonedWriter_DoesNotLeakTheLockOrEnableASecondCommit()
    {
        var store = new BrowserStore();
        var writerA = CreateWriter(store);
        var writerB = CreateWriter(store);
        var observed = (
            await writerA.WriteAsync(new ResourceWriteRequest(Bytes("initial")))
        ).Revision;

        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.StallInsideLock = _ =>
        {
            stalled.TrySetResult();
            return resume.Task;
        };

        // Writer A's circuit disappears while the browser-side operation is in flight. The
        // operation is self-contained, so it still finishes and releases the lock instead of
        // leaving a lock behind or allowing a racy second commit.
        var abandoned = writerA
            .WriteAsync(new ResourceWriteRequest(Bytes("a"), RevisionCondition.Match(observed!)))
            .AsTask();
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondWaiting = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        store.WaitingForLock = () => secondWaiting.TrySetResult();
        var second = writerB
            .WriteAsync(new ResourceWriteRequest(Bytes("b"), RevisionCondition.Match(observed!)))
            .AsTask();
        await secondWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        second.IsCompleted.ShouldBeFalse();

        resume.TrySetResult();
        (await abandoned).Revision.ShouldNotBeNullOrEmpty();
        await Should.ThrowAsync<StateConflictException>(async () => await second);
        (await writerA.ReadAsync()).Content.ToArray().ShouldBe(Bytes("a"));
        store.CommittedCount.ShouldBe(1);
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
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.StallInsideLock = _ =>
        {
            stalled.TrySetResult();
            return resume.Task;
        };

        var first = writerA
            .WriteAsync(new ResourceWriteRequest(Bytes("a"), RevisionCondition.Match(observed!)))
            .AsTask();
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondWaiting = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        store.WaitingForLock = () => secondWaiting.TrySetResult();
        var second = writerB
            .WriteAsync(new ResourceWriteRequest(Bytes("b"), RevisionCondition.Match(observed!)))
            .AsTask();
        await secondWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        resume.TrySetResult();
        (await first).Revision.ShouldNotBeNullOrEmpty();
        await Should.ThrowAsync<StateConflictException>(async () => await second);
        store.CommittedCount.ShouldBe(1);
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
        store.MutateCount.ShouldBe(
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
    public async Task BrowserStorageFailure_IsUnavailableRatherThanMissingAtomicity()
    {
        var store = new BrowserStore { StorageUnavailable = true };
        var writer = CreateWriter(store);
        await Should.ThrowAsync<WebStorageUnavailableException>(async () =>
            await writer.WriteAsync(
                new ResourceWriteRequest(Bytes("value"), RevisionCondition.MustNotExist)
            )
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
        public bool StorageUnavailable { get; set; }

        public int MutateCount { get; private set; }

        public int LastMutateArgumentCount { get; private set; }

        public int SetItemCount { get; private set; }

        public int CommittedCount { get; private set; }

        public TimeSpan ElapsedLogicalTime { get; set; }

        public Func<string, Task>? StallInsideLock { get; set; }

        public Action? WaitingForLock { get; set; }

        public void ResetCounters()
        {
            MutateCount = 0;
            LastMutateArgumentCount = 0;
            SetItemCount = 0;
            CommittedCount = 0;
        }

        public void RecordMutateArguments(int count) => LastMutateArgumentCount = count;

        public void SetRaw(string key, string value)
        {
            lock (_sync)
            {
                _values[key] = value;
            }
        }

        public string? GetItem(string key)
        {
            EnsureAvailable();
            lock (_sync)
            {
                return _values.TryGetValue(key, out var value) ? value : null;
            }
        }

        public Task SetItemAsync(string key, string value)
        {
            EnsureAvailable();
            SetItemCount++;
            lock (_sync)
            {
                _values[key] = value;
            }

            return Task.CompletedTask;
        }

        public async Task<string> MutateAsync(
            string storageName,
            string key,
            string value,
            string? expectedRevision,
            bool mustNotExist
        )
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

            if (StorageUnavailable)
                return "unavailable";
            MutateCount++;
            WaitingForLock?.Invoke();
            await Gate(storageName + "\u0000" + key).WaitAsync().ConfigureAwait(false);
            try
            {
                if (StallInsideLock is { } stall)
                {
                    await stall(key).ConfigureAwait(false);
                }

                string? existing;
                lock (_sync)
                {
                    _values.TryGetValue(key, out existing);
                }

                var exists = !string.IsNullOrEmpty(existing);
                if (mustNotExist)
                {
                    if (exists)
                    {
                        return "conflict";
                    }
                }
                else
                {
                    var revision =
                        exists && existing!.Length > 0 ? CurrentRevision(existing) : null;
                    if (!string.Equals(revision, expectedRevision, StringComparison.Ordinal))
                    {
                        return "conflict";
                    }
                }

                lock (_sync)
                {
                    _values[key] = value;
                }

                CommittedCount++;
                return "committed";
            }
            finally
            {
                Gate(storageName + "\u0000" + key).Release();
            }
        }

        private static string? CurrentRevision(string raw)
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<Envelope>(
                    raw,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                );
                if (envelope?.Content is not null)
                {
                    _ = Convert.FromBase64String(envelope.Content);
                    return envelope.Revision;
                }
            }
            catch (Exception exception) when (exception is JsonException or FormatException)
            {
                // Values that cannot be decoded as envelopes use their raw content hash.
            }
            return Convert
                .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
                .ToLowerInvariant();
        }

        private sealed class Envelope
        {
            public string? Content { get; set; }
            public string? Revision { get; set; }
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
            if (string.Equals(identifier, "configlueWebStorage.mutate", StringComparison.Ordinal))
            {
                _store.RecordMutateArguments(args!.Length);
                var status = await _store
                    .MutateAsync(
                        (string)args[0]!,
                        (string)args[1]!,
                        (string)args[2]!,
                        args[3] as string,
                        (bool)args[4]!
                    )
                    .ConfigureAwait(false);
                return (TValue)(object)status;
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
