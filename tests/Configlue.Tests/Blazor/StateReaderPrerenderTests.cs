using Bunit;
using Configlue;
using Configlue.Hosting.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Tests;

public sealed class StateReaderPrerenderTests
{
    [Test]
    public async Task PrerenderedValue_RestoredWithoutLoadingFlash()
    {
        using var ctx = new BunitContext();
        var store = new FakePrerenderStore();
        var key = StateReader<AppSettings>.PersistenceKeyFor<AppSettings>();
        store.Seed(key, new AppSettings { Label = "prerendered" });
        // The interactive source is slow and newer: restoration must cover the gap.
        var state = new ManualSnapshotState(async () =>
        {
            await Task.Delay(150);
            return Snapshot("interactive");
        });
        ctx.Services.AddSingleton<IReadOnlyState<AppSettings>>(state);
        ctx.Services.AddSingleton<IPrerenderSnapshotStore>(store);

        StateReaderContext<AppSettings>? captured = null;
        var contentRenders = 0;
        var loadingRenders = 0;
        RenderFragment<StateReaderContext<AppSettings>> child = s => builder =>
        {
            captured = s;
            contentRenders++;
            builder.AddContent(0, s.Value.Label);
        };
        RenderFragment loading = builder =>
        {
            loadingRenders++;
            builder.AddContent(0, "LOADING");
        };
        var cut = ctx.Render<StateReader<AppSettings>>(p =>
            p.Add(x => x.ChildContent, child).Add(x => x.LoadingContent, loading)
        );

        cut.WaitForAssertion(() => captured.ShouldNotBeNull());
        // The first interactive frame shows the restored value, never the loading placeholder.
        loadingRenders.ShouldBe(0);
        contentRenders.ShouldBeGreaterThan(0);
        captured!.IsLoading.ShouldBeFalse();

        // The interactive source then reconciles to its newer value.
        cut.WaitForAssertion(() => captured.Value.Label.ShouldBe("interactive"));
        cut.Markup.ShouldContain("interactive");
        cut.Markup.ShouldNotContain("LOADING");
    }

    [Test]
    public void SlowLoadWithoutSeed_RendersLoadingPlaceholder()
    {
        using var ctx = new BunitContext();
        var store = new FakePrerenderStore();
        var state = new ManualSnapshotState(async () =>
        {
            await Task.Delay(150);
            return Snapshot("interactive");
        });
        ctx.Services.AddSingleton<IReadOnlyState<AppSettings>>(state);
        ctx.Services.AddSingleton<IPrerenderSnapshotStore>(store);

        var loadingRenders = 0;
        RenderFragment<StateReaderContext<AppSettings>> child = s => builder =>
            builder.AddContent(0, s.Value.Label);
        RenderFragment loading = builder =>
        {
            loadingRenders++;
            builder.AddContent(0, "LOADING");
        };
        var cut = ctx.Render<StateReader<AppSettings>>(p =>
            p.Add(x => x.ChildContent, child).Add(x => x.LoadingContent, loading)
        );

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("interactive"));
        loadingRenders.ShouldBeGreaterThan(
            0,
            "Without a restored snapshot the loading placeholder must render while waiting."
        );
    }

    [Test]
    public void StalePersistedValue_CannotSuppressFreshRead()
    {
        using var ctx = new BunitContext();
        var store = new FakePrerenderStore();
        var key = StateReader<AppSettings>.PersistenceKeyFor<AppSettings>();
        store.Seed(key, new AppSettings { Label = "stale" });
        var state = new ManualSnapshotState(() => Task.FromResult(Snapshot("fresh")));
        ctx.Services.AddSingleton<IReadOnlyState<AppSettings>>(state);
        ctx.Services.AddSingleton<IPrerenderSnapshotStore>(store);

        StateReaderContext<AppSettings>? captured = null;
        var cut = ctx.Render<StateReader<AppSettings>>(p =>
            p.Add(x => x.ChildContent, s => _ => captured = s)
        );

        cut.WaitForAssertion(() => captured.ShouldNotBeNull());
        cut.WaitForAssertion(() => captured!.Value.Label.ShouldBe("fresh"));
        captured!.LoadFailure.ShouldBeNull();
    }

    [Test]
    public void CorruptPersistedPayload_FallsThroughToFreshRead()
    {
        using var ctx = new BunitContext();
        var store = new FakePrerenderStore { ThrowOnTake = true };
        var state = new ManualSnapshotState(() => Task.FromResult(Snapshot("fresh")));
        ctx.Services.AddSingleton<IReadOnlyState<AppSettings>>(state);
        ctx.Services.AddSingleton<IPrerenderSnapshotStore>(store);

        StateReaderContext<AppSettings>? captured = null;
        var cut = ctx.Render<StateReader<AppSettings>>(p =>
            p.Add(x => x.ChildContent, s => _ => captured = s)
        );

        cut.WaitForAssertion(() => captured.ShouldNotBeNull());
        cut.WaitForAssertion(() => captured!.Value.Label.ShouldBe("fresh"));
    }

    [Test]
    public void PersistDisabled_SkipsStoreEntirely()
    {
        using var ctx = new BunitContext();
        var store = new FakePrerenderStore();
        var key = StateReader<AppSettings>.PersistenceKeyFor<AppSettings>();
        store.Seed(key, new AppSettings { Label = "prerendered" });
        var state = new ManualSnapshotState(() => Task.FromResult(Snapshot("live")));
        ctx.Services.AddSingleton<IReadOnlyState<AppSettings>>(state);
        ctx.Services.AddSingleton<IPrerenderSnapshotStore>(store);

        StateReaderContext<AppSettings>? captured = null;
        var cut = ctx.Render<StateReader<AppSettings>>(p =>
            p.Add(x => x.ChildContent, s => _ => captured = s)
                .Add(x => x.PersistPrerenderedState, false)
        );

        cut.WaitForAssertion(() => captured.ShouldNotBeNull());
        cut.WaitForAssertion(() => captured!.Value.Label.ShouldBe("live"));
        store.TakeCalls.ShouldBe(0);
        store.CallbackCount.ShouldBe(0);
        store.PersistCalls.ShouldBe(0);
    }

    [Test]
    public async Task Prerender_PersistsRenderedValueForHandoff()
    {
        using var ctx = new BunitContext();
        var store = new FakePrerenderStore();
        var state = new ManualSnapshotState(() => Task.FromResult(Snapshot("server")));
        ctx.Services.AddSingleton<IReadOnlyState<AppSettings>>(state);
        ctx.Services.AddSingleton<IPrerenderSnapshotStore>(store);

        StateReaderContext<AppSettings>? captured = null;
        var cut = ctx.Render<StateReader<AppSettings>>(p =>
            p.Add(x => x.ChildContent, s => _ => captured = s)
        );
        cut.WaitForAssertion(() => captured.ShouldNotBeNull());
        cut.WaitForAssertion(() => captured!.Value.Label.ShouldBe("server"));
        store.CallbackCount.ShouldBe(1);

        // The renderer invokes persist callbacks when prerender state is captured.
        await store.RunPersistCallbacksAsync();

        var key = StateReader<AppSettings>.PersistenceKeyFor<AppSettings>();
        store.PersistCalls.ShouldBe(1);
        store.GetPersisted<AppSettings>(key)!.Label.ShouldBe("server");
    }

    [Test]
    public async Task Editor_DoesNotPersistEditSession()
    {
        using var ctx = new BunitContext();
        var store = new FakePrerenderStore();
        ctx.Services.AddSingleton<IPrerenderSnapshotStore>(store);
        var session = new EditSession<AppSettings>(
            new AppSettings { Label = "draft" },
            (_, _) => ValueTaskCompat.FromResult(StateWriteReceipt.Empty)
        );
        ctx.Services.AddSingleton<IConfiglueEditSessions<AppSettings>>(
            new StaticEditSessions<AppSettings>(session)
        );

        StateEditorContext<AppSettings>? captured = null;
        var cut = ctx.Render<StateEditor<AppSettings>>(p =>
            p.Add(x => x.ChildContent, s => _ => captured = s)
        );
        cut.WaitForAssertion(() => captured.ShouldNotBeNull());
        captured!.Value.Label.ShouldBe("draft");

        await store.RunPersistCallbacksAsync();

        store.PersistCalls.ShouldBe(
            0,
            "Edit sessions must open fresh interactively instead of persisting a live draft."
        );
    }

    private static StateSnapshot<AppSettings> Snapshot(string label) =>
        new(new AppSettings { Label = label }, details: null);

    private sealed class ManualSnapshotState(Func<Task<StateSnapshot<AppSettings>>> provider)
        : IReadOnlyState<AppSettings>,
            IConfiglueStateSnapshotRuntime<AppSettings>
    {
        private readonly List<Action<AppSettings>> _listeners = [];

        public IDisposable OnChange(Action<AppSettings> listener)
        {
            ArgumentNullException.ThrowIfNull(listener);
            _listeners.Add(listener);
            return new ActionDisposable(() => _listeners.Remove(listener));
        }

        public ValueTask<AppSettings> GetValueAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Snapshot tests resolve through GetSnapshotAsync.");

        public ValueTask<StateSnapshot<AppSettings>> GetSnapshotAsync(
            CancellationToken cancellationToken = default
        ) => new(provider());
    }

    private sealed class FakePrerenderStore : IPrerenderSnapshotStore
    {
        private readonly Dictionary<string, object?> _seed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, object?> _persisted = new(StringComparer.Ordinal);
        private readonly List<Func<Task>> _callbacks = [];

        public int TakeCalls { get; private set; }

        public int PersistCalls { get; private set; }

        public int CallbackCount => _callbacks.Count;

        public bool ThrowOnTake { get; set; }

        public void Seed<TValue>(string key, TValue value) => _seed[key] = value;

        public TValue? GetPersisted<TValue>(string key) =>
            _persisted.TryGetValue(key, out var boxed) ? (TValue?)boxed : default;

        public bool TryTake<TValue>(string key, out TValue? value)
        {
            TakeCalls++;
            if (ThrowOnTake)
            {
                throw new InvalidOperationException("The persisted payload is corrupt.");
            }

            if (_seed.TryGetValue(key, out var boxed) && boxed is TValue typed)
            {
                _seed.Remove(key);
                value = typed;
                return true;
            }

            value = default;
            return false;
        }

        public void Persist<TValue>(string key, TValue value)
        {
            PersistCalls++;
            _persisted[key] = value;
        }

        public IDisposable OnPersisting(Func<Task> callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            _callbacks.Add(callback);
            return new ActionDisposable(() => _callbacks.Remove(callback));
        }

        public async Task RunPersistCallbacksAsync()
        {
            foreach (var callback in _callbacks.ToArray())
            {
                await callback().ConfigureAwait(false);
            }
        }
    }

    private sealed class StaticEditSessions<T>(EditSession<T> session) : IConfiglueEditSessions<T>
        where T : class
    {
        public ValueTask<EditSession<T>> OpenEditSessionAsync(
            CancellationToken cancellationToken = default
        ) => ValueTaskCompat.FromResult(session);

        public ValueTask<EditSession<T>> OpenEditSessionAsync(
            StateWritePlan writePlan,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(writePlan);
            return ValueTaskCompat.FromResult(session);
        }
    }

    private sealed class ActionDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
