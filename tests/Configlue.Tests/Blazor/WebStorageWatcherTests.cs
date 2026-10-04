using Configlue;
using Configlue.Hosting.Blazor;
using Configlue.Resources;
using Microsoft.JSInterop;

namespace Configlue.Tests;

public sealed class WebStorageWatcherTests
{
    [Test]
    public async Task SameContextWrite_WakesLocalWatcher()
    {
        var browser = new FakeBrowser();
        await using var resource = new WebStorageResource(browser.Runtime, WebStorageKind.Local, "k");
        var first = await resource.WriteAsync(default, Write("one"));
        var wait = resource.WaitForChangeAsync(default, first.Revision).AsTask();
        await Task.Delay(50);
        wait.IsCompleted.ShouldBeFalse();

        await resource.WriteAsync(default, Write("two"));

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task SameContextConditionalWrite_WakesLocalWatcher()
    {
        var browser = new FakeBrowser();
        await using var resource = new WebStorageResource(browser.Runtime, WebStorageKind.Local, "k");
        var first = await resource.WriteAsync(default, Write("one"));
        var wait = resource.WaitForChangeAsync(default, first.Revision).AsTask();

        await resource.WriteAsync(
            default,
            new ResourceWriteRequest(Bytes("two"), RevisionCondition.Match(first.Revision!))
        );

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ExternalStorageEvent_WakesMatchingWatcher()
    {
        var browser = new FakeBrowser();
        await using var resource = new WebStorageResource(browser.Runtime, WebStorageKind.Local, "k");
        var first = await resource.WriteAsync(default, Write("one"));
        var wait = resource.WaitForChangeAsync(default, first.Revision).AsTask();
        await Task.Delay(50);

        browser.RaiseStorageEvent("localStorage", "k");

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ExternalStorageEvent_UnrelatedKeyDoesNotWake()
    {
        var browser = new FakeBrowser();
        await using var resource = new WebStorageResource(browser.Runtime, WebStorageKind.Local, "k");
        var first = await resource.WriteAsync(default, Write("one"));
        using var cancellation = new CancellationTokenSource();
        var wait = resource.WaitForChangeAsync(default, first.Revision, cancellation.Token).AsTask();

        browser.RaiseStorageEvent("localStorage", "other");
        await Task.Delay(200);

        wait.IsCompleted.ShouldBeFalse();
        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await wait);
    }

    [Test]
    public async Task ExternalStorageEvent_UnrelatedKindDoesNotWake()
    {
        var browser = new FakeBrowser();
        await using var resource = new WebStorageResource(browser.Runtime, WebStorageKind.Local, "k");
        var first = await resource.WriteAsync(default, Write("one"));
        using var cancellation = new CancellationTokenSource();
        var wait = resource.WaitForChangeAsync(default, first.Revision, cancellation.Token).AsTask();

        browser.RaiseStorageEvent("sessionStorage", "k");
        await Task.Delay(200);

        wait.IsCompleted.ShouldBeFalse();
        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await wait);
    }

    [Test]
    public async Task ExternalStorageEvent_SessionResourceIgnoresLocalKind()
    {
        var browser = new FakeBrowser();
        await using var resource = new WebStorageResource(
            browser.Runtime,
            WebStorageKind.Session,
            "k"
        );
        var first = await resource.WriteAsync(default, Write("one"));
        using var cancellation = new CancellationTokenSource();
        var wait = resource.WaitForChangeAsync(default, first.Revision, cancellation.Token).AsTask();

        browser.RaiseStorageEvent("localStorage", "k");
        await Task.Delay(200);
        wait.IsCompleted.ShouldBeFalse();

        browser.RaiseStorageEvent("sessionStorage", "k");
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ContextDerivedKeys_AreIsolated()
    {
        var browser = new FakeBrowser();
        await using var resource = new WebStorageResource(
            browser.Runtime,
            WebStorageKind.Local,
            "base"
        );
        var subject = new FakeSubject("user-a");
        var other = new ConfiglueResourceContext(
            subject,
            ResourceKey.From("a"),
            RouteKey.Default
        );
        var first = await resource.WriteAsync(ConfiglueResourceContext.Default, Write("one"));
        using var cancellation = new CancellationTokenSource();
        var wait = resource
            .WaitForChangeAsync(ConfiglueResourceContext.Default, first.Revision, cancellation.Token)
            .AsTask();

        // A non-default resource key derives a distinct storage key ("base:a").
        browser.RaiseStorageEvent("localStorage", "base:a");
        await Task.Delay(200);
        wait.IsCompleted.ShouldBeFalse();

        browser.RaiseStorageEvent("localStorage", "base");
        await wait.WaitAsync(TimeSpan.FromSeconds(5));

        var derived = await resource.WriteAsync(other, Write("scoped"));
        derived.Revision.ShouldNotBeNullOrEmpty();
        browser.Local["base:a"].ShouldNotBeNullOrEmpty();
    }

    [Test]
    public async Task CustomKeySelector_WinsOverDerivedKeys()
    {
        var browser = new FakeBrowser();
        await using var resource = new WebStorageResource(
            browser.Runtime,
            WebStorageKind.Local,
            "base",
            _ => "custom"
        );
        var first = await resource.WriteAsync(ConfiglueResourceContext.Default, Write("one"));
        using var cancellation = new CancellationTokenSource();
        var wait = resource
            .WaitForChangeAsync(ConfiglueResourceContext.Default, first.Revision, cancellation.Token)
            .AsTask();

        browser.RaiseStorageEvent("localStorage", "base");
        browser.RaiseStorageEvent("localStorage", "base:a");
        await Task.Delay(200);
        wait.IsCompleted.ShouldBeFalse();

        browser.RaiseStorageEvent("localStorage", "custom");
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ClearEvent_WakesSameKindOnly()
    {
        var browser = new FakeBrowser();
        await using var local = new WebStorageResource(browser.Runtime, WebStorageKind.Local, "k");
        await using var session = new WebStorageResource(
            browser.Runtime,
            WebStorageKind.Session,
            "k"
        );
        var localRevision = (await local.WriteAsync(default, Write("one"))).Revision;
        var sessionRevision = (await session.WriteAsync(default, Write("one"))).Revision;
        using var cancellation = new CancellationTokenSource();
        var localWait = local
            .WaitForChangeAsync(default, localRevision, cancellation.Token)
            .AsTask();
        var sessionWait = session
            .WaitForChangeAsync(default, sessionRevision, cancellation.Token)
            .AsTask();

        browser.RaiseStorageEvent("localStorage", key: null);

        await localWait.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        sessionWait.IsCompleted.ShouldBeFalse();
        await cancellation.CancelAsync();
    }

    [Test]
    public async Task MultipleWaiters_AllWakeOnOneSignal()
    {
        var browser = new FakeBrowser();
        await using var resource = new WebStorageResource(browser.Runtime, WebStorageKind.Local, "k");
        var first = await resource.WriteAsync(default, Write("one"));
        var waits = Enumerable
            .Range(0, 3)
            .Select(_ => resource.WaitForChangeAsync(default, first.Revision).AsTask())
            .ToArray();

        browser.RaiseStorageEvent("localStorage", "k");

        await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(5));
        resource.WaiterCountForTests.ShouldBe(0);
    }

    [Test]
    public async Task StaleObservedRevision_ReturnsImmediately()
    {
        var browser = new FakeBrowser();
        await using var resource = new WebStorageResource(browser.Runtime, WebStorageKind.Local, "k");
        await resource.WriteAsync(default, Write("one"));

        await resource
            .WaitForChangeAsync(default, "stale")
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Cancellation_PreCanceledTokenThrowsImmediately()
    {
        var browser = new FakeBrowser();
        await using var resource = new WebStorageResource(browser.Runtime, WebStorageKind.Local, "k");
        var first = await resource.WriteAsync(default, Write("one"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await resource.WaitForChangeAsync(default, first.Revision, cancellation.Token)
        );
    }

    [Test]
    public async Task Disposal_ReleasesJsSubscription()
    {
        var browser = new FakeBrowser();
        var resource = new WebStorageResource(browser.Runtime, WebStorageKind.Local, "k");
        var first = await resource.WriteAsync(default, Write("one"));
        using var cancellation = new CancellationTokenSource();
        var wait = resource.WaitForChangeAsync(default, first.Revision, cancellation.Token).AsTask();
        await WaitUntilAsync(() => browser.SubscribeCount == 1, "The waiter should subscribe.");
        browser.SubscribeCount.ShouldBe(1);

        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await wait);
        await resource.DisposeAsync();

        browser.UnsubscribeCount.ShouldBe(1);
        browser.ReceiverCount.ShouldBe(0);
        browser.ModuleDisposeCount.ShouldBe(1);
        await Should.ThrowAsync<ObjectDisposedException>(async () =>
            await resource.WaitForChangeAsync(default, first.Revision)
        );
        await Should.ThrowAsync<ObjectDisposedException>(async () =>
            await resource.WriteAsync(default, Write("late"))
        );
    }

    [Test]
    public async Task Disposal_CancelsPendingWaiters()
    {
        var browser = new FakeBrowser();
        var resource = new WebStorageResource(browser.Runtime, WebStorageKind.Local, "k");
        var first = await resource.WriteAsync(default, Write("one"));
        var wait = resource.WaitForChangeAsync(default, first.Revision).AsTask();
        await WaitUntilAsync(() => browser.SubscribeCount == 1, "The waiter should subscribe.");

        await resource.DisposeAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await wait);
    }

    [Test]
    public async Task WatchDisabled_PerformsNoRegistration()
    {
        var browser = new FakeBrowser();
        await using var resource = new WebStorageResource(
            browser.Runtime,
            WebStorageKind.Local,
            "k",
            keySelector: null,
            watchChanges: false
        );
        var first = await resource.WriteAsync(default, Write("one"));
        using var cancellation = new CancellationTokenSource();
        var wait = resource.WaitForChangeAsync(default, first.Revision, cancellation.Token).AsTask();
        await Task.Delay(100);

        wait.IsCompleted.ShouldBeFalse();
        browser.ImportCount.ShouldBe(0);
        browser.SubscribeCount.ShouldBe(0);

        await resource.WriteAsync(default, Write("two"));
        await Task.Delay(100);
        wait.IsCompleted.ShouldBeFalse();

        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await wait);
        new WebStorageSourceOptions().WatchChanges.ShouldBeTrue();
    }

    [Test]
    public async Task Module_ImportedLazilyAndReused()
    {
        var browser = new FakeBrowser();
        await using var resource = new WebStorageResource(browser.Runtime, WebStorageKind.Local, "k");
        browser.ImportCount.ShouldBe(0);

        await resource.ReadAsync(default);
        browser.ImportCount.ShouldBe(
            0,
            "Reads use direct storage access and must not import the module."
        );

        await resource.WriteAsync(default, Write("one"));
        browser.ImportCount.ShouldBe(
            0,
            "Unconditional writes use direct storage access and must not import the module."
        );

        var first = await resource.ReadAsync(default);
        var wait = resource.WaitForChangeAsync(default, first.Revision).AsTask();
        await WaitUntilAsync(() => browser.SubscribeCount == 1, "The waiter should subscribe.");
        browser.ImportCount.ShouldBe(1);

        await resource.WriteAsync(
            default,
            new ResourceWriteRequest(Bytes("two"), RevisionCondition.Match(first.Revision!))
        );
        browser.ImportCount.ShouldBe(1, "The module import is cached and reused.");

        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        browser.SubscribeCount.ShouldBe(
            1,
            "One subscription fans out to every waiter on the resource."
        );
    }

    [Test]
    public async Task UnavailableJavascript_WaitsUntilCanceledWithoutThrowing()
    {
        var browser = new FakeBrowser { JavascriptAvailable = false };
        await using var resource = new WebStorageResource(browser.Runtime, WebStorageKind.Local, "k");
        using var cancellation = new CancellationTokenSource();
        var wait = resource.WaitForChangeAsync(default, "anything", cancellation.Token).AsTask();
        await Task.Delay(100);

        wait.IsCompleted.ShouldBeFalse();
        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await wait);

        // Teardown after a disconnect stays silent.
        await resource.DisposeAsync();
    }

    private static ResourceWriteRequest Write(string value) =>
        new(Bytes(value));

    private static byte[] Bytes(string value) =>
        System.Text.Encoding.UTF8.GetBytes(value);

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(message);
            }

            await Task.Delay(10);
        }
    }

    private sealed record FakeSubject(string Id) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From(Id);
    }

    private sealed class FakeBrowser
    {
        public Dictionary<string, string> Local { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Session { get; } = new(StringComparer.Ordinal);

        public bool JavascriptAvailable { get; set; } = true;

        public int ImportCount { get; private set; }

        public int SubscribeCount { get; private set; }

        public int UnsubscribeCount { get; private set; }

        public int ModuleDisposeCount { get; private set; }

        public int ReceiverCount => _receivers.Count;

        public FakeRuntime Runtime { get; }

        private readonly Dictionary<
            string,
            DotNetObjectReference<WebStorageChangeReceiver>
        > _receivers = new(StringComparer.Ordinal);

        public FakeBrowser() => Runtime = new FakeRuntime(this);

        public void RaiseStorageEvent(string? storageName, string? key)
        {
            foreach (var receiver in _receivers.Values.ToArray())
            {
                receiver.Value.OnStorageChanged(storageName, key);
            }
        }

        public sealed class FakeRuntime(FakeBrowser owner) : IJSRuntime
        {
            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
                InvokeAsync<TValue>(identifier, default, args);

            public ValueTask<TValue> InvokeAsync<TValue>(
                string identifier,
                CancellationToken cancellationToken,
                object?[]? args
            )
            {
                if (string.Equals(identifier, "import", StringComparison.Ordinal))
                {
                    owner.ImportCount++;
                    owner.EnsureAvailable();
                    return new ValueTask<TValue>((TValue)(object)new FakeModule(owner));
                }

                if (identifier.EndsWith(".getItem", StringComparison.Ordinal))
                {
                    owner.EnsureAvailable();
                    var area = identifier.StartsWith(
                        "localStorage",
                        StringComparison.Ordinal
                    )
                        ? owner.Local
                        : owner.Session;
                    area.TryGetValue((string)args![0]!, out var value);
                    return ValueTaskCompat.FromResult((TValue)(object?)value!);
                }

                if (identifier.EndsWith(".setItem", StringComparison.Ordinal))
                {
                    owner.EnsureAvailable();
                    var area = identifier.StartsWith(
                        "localStorage",
                        StringComparison.Ordinal
                    )
                        ? owner.Local
                        : owner.Session;
                    area[(string)args![0]!] = (string)args[1]!;
                    return ValueTaskCompat.FromResult(default(TValue)!);
                }

                throw new NotSupportedException(identifier);
            }
        }

        private sealed class FakeModule(FakeBrowser owner) : IJSObjectReference
        {
            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
                InvokeAsync<TValue>(identifier, default, args);

            public ValueTask<TValue> InvokeAsync<TValue>(
                string identifier,
                CancellationToken cancellationToken,
                object?[]? args
            )
            {
                owner.EnsureAvailable();
                if (string.Equals(identifier, "subscribeStorageChanges", StringComparison.Ordinal))
                {
                    owner.SubscribeCount++;
                    owner._receivers[(string)args![0]!] = (
                        DotNetObjectReference<WebStorageChangeReceiver>
                    )args[1]!;
                    // Called through InvokeVoidAsync: the module reference expects no payload.
                    return ValueTaskCompat.FromResult(default(TValue)!);
                }

                if (
                    string.Equals(identifier, "unsubscribeStorageChanges", StringComparison.Ordinal)
                )
                {
                    owner.UnsubscribeCount++;
                    owner._receivers.Remove((string)args![0]!);

                    return ValueTaskCompat.FromResult(default(TValue)!);
                }

                if (string.Equals(identifier, "mutate", StringComparison.Ordinal))
                {
                    var area =
                        string.Equals((string)args![0]!, "sessionStorage", StringComparison.Ordinal)
                            ? owner.Session
                            : owner.Local;
                    area[(string)args[1]!] = (string)args[2]!;
                    return new ValueTask<TValue>((TValue)(object)"committed");
                }

                throw new NotSupportedException(identifier);
            }

            ValueTask IAsyncDisposable.DisposeAsync()
            {
                owner.ModuleDisposeCount++;
                return ValueTask.CompletedTask;
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
}
