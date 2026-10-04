using Configlue;
using Configlue.Sources;
using Configlue.Testing;

namespace Configlue.DevTools.Tests;

public sealed class ViewerSessionTests
{
    [Test]
    public async Task Lifecycle_LoadsInitialDocumentAndDisposesDeterministically()
    {
        await using var context = SessionFixtures.CreateViewerContext();
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = new ConfiglueDevToolsViewerSession<DevToolsViewerSettings>(state);

        session.Current.ShouldBeNull();
        session.DocumentVersion.ShouldBe(0);
        session.SchemaSetup.SchemaUri.ShouldContain("devtools-viewer");

        var document = await session.LoadAsync();
        document.Json.ShouldContain("\"Theme\": \"Dark\"");
        session.Current.ShouldNotBeNull();
        session.DocumentVersion.ShouldBe(1);

        var changed = false;
        session.Changed += () => changed = true;
        session.Dispose();
        _ = changed;

        await Should.ThrowAsync<ObjectDisposedException>(async () => await session.LoadAsync());
        await Should.ThrowAsync<ObjectDisposedException>(async () => await session.RefreshAsync());
        session.Dispose();
    }

    [Test]
    public async Task WatchUpdate_FlowsThroughMinimalEdits()
    {
        var store = new InMemoryStateSource<DevToolsViewerSettings.Fragment>(
            new DevToolsViewerSettings.Fragment { Theme = Optional<string>.Present("Dark") }
        );
        var builder = new ConfiglueBuilder();
        builder.Add<DevToolsViewerSettings>(model =>
            model.Sources(sources =>
                sources.Add(
                    new StateSource<DevToolsViewerSettings.Fragment>(
                        "live",
                        store,
                        new StateSourceOptions<DevToolsViewerSettings.Fragment>
                        {
                            Writer = store,
                            Watcher = store,
                        }
                    )
                )
            )
        );
        await using var context = builder.CreateContext();
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = new ConfiglueDevToolsViewerSession<DevToolsViewerSettings>(state);
        var loaded = await session.LoadAsync();
        loaded.Json.ShouldContain("\"Dark\"");

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Changed += () => signal.TrySetResult();
        store.Set(
            new DevToolsViewerSettings.Fragment { Theme = Optional<string>.Present("Light") }
        );

        await signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var refresh = await PollForChangeAsync(
            session,
            static result => result.TextChanged,
            TimeSpan.FromSeconds(5)
        );
        refresh.TextChanged.ShouldBeTrue();
        refresh.TextEdits.ShouldNotBeEmpty();
        refresh.Document.Json.ShouldContain("\"Light\"");
        refresh.Document.DocumentVersion.ShouldBeGreaterThan(loaded.DocumentVersion);

        // Minimal edits: the replacement text is far smaller than the full document.
        var editText = string.Concat(refresh.TextEdits.Select(static edit => edit.Text));
        editText.Length.ShouldBeLessThan(refresh.Document.Json.Length);
        foreach (var edit in refresh.TextEdits)
        {
            edit.Range.IsValid.ShouldBeTrue();
        }
    }

    [Test]
    public async Task DecorationOnlyUpdate_CarriesNoDocumentPayload()
    {
        var effective = new InMemoryStateSource<DevToolsViewerSettings.Fragment>(
            new DevToolsViewerSettings.Fragment { Theme = Optional<string>.Present("Dark") }
        );
        var shadowed = new InMemoryStateSource<DevToolsViewerSettings.Fragment>(
            new DevToolsViewerSettings.Fragment { Theme = Optional<string>.Present("Shadow") }
        );
        var builder = new ConfiglueBuilder();
        builder.Add<DevToolsViewerSettings>(model =>
            model.Sources(sources =>
            {
                sources.Add(
                    new StateSource<DevToolsViewerSettings.Fragment>(
                        "primary",
                        effective,
                        new StateSourceOptions<DevToolsViewerSettings.Fragment>
                        {
                            Priority = 100,
                            Writer = effective,
                            Watcher = effective,
                        }
                    )
                );
                sources.Add(
                    new StateSource<DevToolsViewerSettings.Fragment>(
                        "fallback",
                        shadowed,
                        new StateSourceOptions<DevToolsViewerSettings.Fragment>
                        {
                            FallbackCondition =
                                StateFallbackCondition.NotFoundOrUnavailable
                                | StateFallbackCondition.InvalidPayload,
                            Watcher = shadowed,
                        }
                    )
                );
            })
        );
        await using var context = builder.CreateContext();
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = new ConfiglueDevToolsViewerSession<DevToolsViewerSettings>(state);
        await session.LoadAsync();

        // A shadowed source going dark changes provenance, not effective JSON.
        shadowed.SetUnavailable();
        var refresh = await PollForChangeAsync(
            session,
            static result => result.DecorationsChanged,
            TimeSpan.FromSeconds(5)
        );

        refresh.TextChanged.ShouldBeFalse();
        refresh.TextEdits.ShouldBeEmpty();
        refresh.DecorationsChanged.ShouldBeTrue();
        refresh.Document.Json.ShouldContain("\"Dark\"");
    }

    [Test]
    public async Task ContributionProjection_UsesCachedSnapshotWithoutBackendReads()
    {
        const string password = "session-pw-5x";
        await using var context = SessionFixtures.CreateSecretViewerContext(password);
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = new ConfiglueDevToolsViewerSession<DevToolsViewerSettings>(state);
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await Task.Yield();
            session.GetContributionJson("Database.Password");
        });

        await session.LoadAsync();
        var json = session.GetContributionJson("Database.Password");
        json.ShouldContain(ConfiglueSecrets.RedactedText);
        json.ShouldNotContain(password);
        json.ShouldContain("Database.Password");
    }

    [Test]
    public async Task IdenticalRefresh_ReturnsNoWork()
    {
        await using var context = SessionFixtures.CreateViewerContext();
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = new ConfiglueDevToolsViewerSession<DevToolsViewerSettings>(state);
        var loaded = await session.LoadAsync();
        var refresh = await session.RefreshAsync();

        refresh.TextChanged.ShouldBeFalse();
        refresh.DecorationsChanged.ShouldBeFalse();
        refresh.TextEdits.ShouldBeEmpty();
        refresh.Document.ShouldBe(loaded);
    }

    private static async Task<ConfiglueViewerRefreshResult> PollForChangeAsync(
        ConfiglueDevToolsViewerSession<DevToolsViewerSettings> session,
        Func<ConfiglueViewerRefreshResult, bool> ready,
        TimeSpan timeout
    )
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var refresh = await session.RefreshAsync();
            if (ready(refresh))
            {
                return refresh;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The viewer session did not observe the change.");
            }

            await Task.Delay(50);
        }
    }

    private static class SessionFixtures
    {
        public static ConfiglueContext CreateViewerContext()
        {
            var builder = new ConfiglueBuilder();
            builder.Add<DevToolsViewerSettings>(model =>
                model.Sources(sources =>
                    sources.Add(
                        DevToolsFixtures.MemorySource(
                            new DevToolsViewerSettings.Fragment
                            {
                                Theme = Optional<string>.Present("Dark"),
                                RetryCount = Optional<int>.Present(5),
                            }
                        )
                    )
                )
            );
            return builder.CreateContext();
        }

        public static ConfiglueContext CreateSecretViewerContext(string password)
        {
            var builder = new ConfiglueBuilder();
            builder.Add<DevToolsViewerSettings>(model =>
                model.Sources(sources =>
                    sources.Add(
                        DevToolsFixtures.MemorySource(
                            new DevToolsViewerSettings.Fragment
                            {
                                Database = Optional<DevToolsViewerDatabase.Fragment?>.Present(
                                    new DevToolsViewerDatabase.Fragment
                                    {
                                        Password = Optional<string>.Present(password),
                                    }
                                ),
                            }
                        )
                    )
                )
            );
            return builder.CreateContext();
        }
    }
}
