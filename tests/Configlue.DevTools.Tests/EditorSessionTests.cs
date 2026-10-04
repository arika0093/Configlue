using System.Text.Json.Nodes;
using Configlue.State;
using Configlue.Testing;

namespace Configlue.DevTools.Tests;

public sealed class EditorSessionTests
{
    [Test]
    public async Task Sync_SemanticallyEquivalentDraftProducesNoChanges()
    {
        // Formatting, member order, and JSON string spelling do not change the
        // effective value, so none of them may mark the session dirty.
        await using var context = ViewerFixtures.CreateViewerContext();
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        var reformatted = JsonNode
            .Parse(session.SessionStartDocument.Json)!
            .ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var formatOnly = await session.SyncDraftAsync(reformatted);
        formatOnly.Success.ShouldBeTrue();
        formatOnly.ModifiedCount.ShouldBe(0);

        var node = JsonNode.Parse(session.SessionStartDocument.Json)!.AsObject();
        var reversed = new JsonObject();
        foreach (var (key, child) in node.Reverse())
        {
            reversed.Add(key, child?.DeepClone());
        }

        var reordered = await session.SyncDraftAsync(reversed.ToJsonString());
        reordered.Success.ShouldBeTrue();
        reordered.ModifiedCount.ShouldBe(0);

        // \u0044 is 'D': same effective value, different spelling.
        var escaped = session.SessionStartDocument.Json.Replace("\"Dark\"", "\"\\u0044ark\"");
        escaped.ShouldNotBe(session.SessionStartDocument.Json);
        var respelled = await session.SyncDraftAsync(escaped);
        respelled.Success.ShouldBeTrue();
        respelled.ModifiedCount.ShouldBe(0);

        session.HasLocalChanges.ShouldBeFalse();
    }

    [Test]
    public async Task Sync_ScalarEditTracksMemberPath()
    {
        await using var context = ViewerFixtures.CreateViewerContext();
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        var draft = session.SessionStartDocument.Json.Replace("\"Dark\"", "\"Light\"");
        var result = await session.SyncDraftAsync(draft);

        result.Success.ShouldBeTrue();
        result.Category.ShouldBe(ConfiglueEditorFailureCategory.None);
        result.ModifiedCount.ShouldBe(1);
        result.ChangedPaths.Select(static path => path.MemberPath).ShouldBe(["Theme"]);
        result.HasChanges.ShouldBeTrue();
        session.HasLocalChanges.ShouldBeTrue();
        session.ModifiedPaths.Count.ShouldBe(1);
    }

    [Test]
    public async Task SyncAndCommit_NestedEditRoutesNormally()
    {
        await using var context = ViewerFixtures.CreateViewerContext();
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        var draft = session.SessionStartDocument.Json.Replace("5432", "5433");
        var sync = await session.SyncDraftAsync(draft);
        sync.Success.ShouldBeTrue();
        sync.ChangedPaths.Select(static path => path.MemberPath).ShouldBe(["Database.Port"]);

        var save = await session.CommitAsync();
        save.Committed.ShouldBeTrue();
        save.Category.ShouldBe(ConfiglueEditorFailureCategory.None);
        save.Receipt.ShouldNotBeNull();
        save.Receipt!.Sources.Count.ShouldBe(1);
        save.CommittedPaths.ShouldBe(["Database.Port"]);

        (await state.GetValueAsync()).Database!.Port.ShouldBe(5433);
        session.HasLocalChanges.ShouldBeFalse();
        session.ModifiedPaths.ShouldBeEmpty();
    }

    [Test]
    public async Task Sync_NullSetSemantics()
    {
        await using var context = ViewerFixtures.CreateViewerContext(notes: "hello");
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        var node = JsonNode.Parse(session.SessionStartDocument.Json)!.AsObject();
        node["Notes"] = null;
        var sync = await session.SyncDraftAsync(node.ToJsonString());
        sync.Success.ShouldBeTrue();
        sync.ChangedPaths.Select(static path => path.MemberPath).ShouldBe(["Notes"]);

        var save = await session.CommitAsync();
        save.Committed.ShouldBeTrue();
        (await state.GetValueAsync()).Notes.ShouldBeNull();
    }

    [Test]
    public async Task Sync_NullNestedModelUnsetsSubtree()
    {
        await using var context = ViewerFixtures.CreateViewerContext();
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        var node = JsonNode.Parse(session.SessionStartDocument.Json)!.AsObject();
        node["Database"] = null;
        var sync = await session.SyncDraftAsync(node.ToJsonString());
        sync.Success.ShouldBeTrue();
        sync.ChangedPaths.Select(static path => path.MemberPath).ShouldBe(["Database"]);

        var save = await session.CommitAsync();
        save.Committed.ShouldBeTrue();
        (await state.GetValueAsync()).Database.ShouldBeNull();
    }

    [Test]
    public async Task Sync_AbsentMemberResetsToDefault()
    {
        await using var context = ViewerFixtures.CreateViewerContext(theme: "Dark");
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        // Removing a member is an explicit reset-to-default, never a guess:
        // Theme falls back to the model default "Light".
        var node = JsonNode.Parse(session.SessionStartDocument.Json)!.AsObject();
        node.Remove("Theme");
        var draft = node.ToJsonString();
        draft.ShouldNotContain("Theme");

        var sync = await session.SyncDraftAsync(draft);
        sync.Success.ShouldBeTrue();
        sync.ChangedPaths.Select(static path => path.MemberPath).ShouldBe(["Theme"]);

        var save = await session.CommitAsync();
        save.Committed.ShouldBeTrue();
        (await state.GetValueAsync()).Theme.ShouldBe("Light");
    }

    [Test]
    public async Task SyncAndCommit_CollectionReplace()
    {
        await using var context = ViewerFixtures.CreateViewerContext();
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        var node = JsonNode.Parse(session.SessionStartDocument.Json)!.AsObject();
        node["Tags"]![1] = "green";
        var sync = await session.SyncDraftAsync(node.ToJsonString());
        sync.Success.ShouldBeTrue();
        // Scalar collections merge as whole members under Configlue patch semantics.
        sync.ChangedPaths.Select(static path => path.MemberPath).ShouldBe(["Tags"]);

        var save = await session.CommitAsync();
        save.Committed.ShouldBeTrue();
        (await state.GetValueAsync()).Tags.ShouldBe(["web", "green"]);
    }

    [Test]
    public async Task Sync_UnknownMemberIsSchemaError()
    {
        await using var context = ViewerFixtures.CreateViewerContext();
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        var node = JsonNode.Parse(session.SessionStartDocument.Json)!.AsObject();
        node["Nope"] = 1;

        var result = await session.SyncDraftAsync(node.ToJsonString());
        result.Success.ShouldBeFalse();
        result.Category.ShouldBe(ConfiglueEditorFailureCategory.Schema);
        result.Errors.ShouldNotBeEmpty();
        session.HasLocalChanges.ShouldBeFalse();
    }

    [Test]
    public async Task Sync_WrongTypeIsSchemaError()
    {
        await using var context = ViewerFixtures.CreateViewerContext();
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        var node = JsonNode.Parse(session.SessionStartDocument.Json)!.AsObject();
        node["RetryCount"] = "many";

        var result = await session.SyncDraftAsync(node.ToJsonString());
        result.Success.ShouldBeFalse();
        result.Category.ShouldBe(ConfiglueEditorFailureCategory.Schema);
        session.HasLocalChanges.ShouldBeFalse();
    }

    [Test]
    public async Task Sync_MalformedJsonIsParseError()
    {
        await using var context = ViewerFixtures.CreateViewerContext();
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        var result = await session.SyncDraftAsync("{ \"Theme\": ");
        result.Success.ShouldBeFalse();
        result.Category.ShouldBe(ConfiglueEditorFailureCategory.Parse);
        session.HasLocalChanges.ShouldBeFalse();
    }

    [Test]
    public async Task Sync_ReadOnlyMemberRejected()
    {
        // RetryCount is shadowed by a higher-priority read-only contribution while
        // Theme stays editable through the writable source.
        var shadow = new InMemoryStateSource<DevToolsViewerSettings.Fragment>(
            new DevToolsViewerSettings.Fragment { RetryCount = Optional<int>.Present(11) }
        );
        var writableStore = new InMemoryStateSource<DevToolsViewerSettings.Fragment>(
            new DevToolsViewerSettings.Fragment
            {
                Theme = Optional<string>.Present("Dark"),
                RetryCount = Optional<int>.Present(5),
            }
        );
        var builder = new ConfiglueBuilder();
        builder.Add<DevToolsViewerSettings>(model =>
            model.Sources(sources =>
            {
                sources.Add(
                    new StateSource<DevToolsViewerSettings.Fragment>(
                        "shadow",
                        shadow,
                        new StateSourceOptions<DevToolsViewerSettings.Fragment> { Priority = 100 }
                    )
                );
                sources.Add(
                    new StateSource<DevToolsViewerSettings.Fragment>(
                        "writable",
                        writableStore,
                        new StateSourceOptions<DevToolsViewerSettings.Fragment>
                        {
                            Writer = writableStore,
                        }
                    )
                );
            })
        );
        await using var context = builder.CreateContext();
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        (await state.GetValueAsync()).RetryCount.ShouldBe(11);

        var node = JsonNode.Parse(session.SessionStartDocument.Json)!.AsObject();
        node["RetryCount"] = 12;
        var blocked = await session.SyncDraftAsync(node.ToJsonString());
        blocked.Success.ShouldBeFalse();
        blocked.Category.ShouldBe(ConfiglueEditorFailureCategory.NonEditable);
        blocked.Errors.ShouldNotBeEmpty();
        session.HasLocalChanges.ShouldBeFalse();

        // Editable members still synchronize in the same session.
        var editable = JsonNode.Parse(session.SessionStartDocument.Json)!.AsObject();
        editable["Theme"] = "Light";
        var allowed = await session.SyncDraftAsync(editable.ToJsonString());
        allowed.Success.ShouldBeTrue();
        allowed.ChangedPaths.Select(static path => path.MemberPath).ShouldBe(["Theme"]);

        var save = await session.CommitAsync();
        save.Committed.ShouldBeTrue();
        var value = await state.GetValueAsync();
        value.Theme.ShouldBe("Light");
        value.RetryCount.ShouldBe(11);
    }

    [Test]
    public async Task Open_FullyReadOnlyStateThrows()
    {
        var store = new InMemoryStateSource<DevToolsViewerSettings.Fragment>(
            new DevToolsViewerSettings.Fragment { RetryCount = Optional<int>.Present(11) }
        );
        var builder = new ConfiglueBuilder();
        builder.Add<DevToolsViewerSettings>(model =>
            model.Sources(sources =>
                sources.Add(
                    new StateSource<DevToolsViewerSettings.Fragment>(
                        "sealed",
                        store,
                        new StateSourceOptions<DevToolsViewerSettings.Fragment>()
                    )
                )
            )
        );
        await using var context = builder.CreateContext();
        var state = context.GetState<DevToolsViewerSettings>();

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(state)
        );
        exception.Message.ShouldContain("no writable source");
    }

    [Test]
    public async Task Commit_SingleSourceReceipt()
    {
        await using var context = ViewerFixtures.CreateViewerContext();
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        var node = JsonNode.Parse(session.SessionStartDocument.Json)!.AsObject();
        node["Theme"] = "Light";
        node["RetryCount"] = 6;
        (await session.SyncDraftAsync(node.ToJsonString())).Success.ShouldBeTrue();

        var save = await session.CommitAsync();
        save.Committed.ShouldBeTrue();
        save.Receipt.ShouldNotBeNull();
        save.Receipt!.Sources.Count.ShouldBe(1);
        save.Receipt.PhysicalWriteCount.ShouldBe(1);
        save.Receipt.IsAtomic.ShouldBeTrue();

        var value = await state.GetValueAsync();
        value.Theme.ShouldBe("Light");
        value.RetryCount.ShouldBe(6);
    }

    [Test]
    public async Task Commit_MultiSourceRoutingWithoutFlattening()
    {
        var themeStore = new InMemoryStateSource<DevToolsViewerSettings.Fragment>(
            new DevToolsViewerSettings.Fragment { Theme = Optional<string>.Present("Dark") }
        );
        var retryStore = new InMemoryStateSource<DevToolsViewerSettings.Fragment>(
            new DevToolsViewerSettings.Fragment { RetryCount = Optional<int>.Present(5) }
        );
        var composite = new CompositeStateSource<DevToolsViewerSettings.Fragment>(
            new StateSourceSet<DevToolsViewerSettings.Fragment>([
                new StateSource<DevToolsViewerSettings.Fragment>(
                    "theme-source",
                    themeStore,
                    new StateSourceOptions<DevToolsViewerSettings.Fragment>
                    {
                        Writer = themeStore,
                        Watcher = themeStore,
                    }
                ),
                new StateSource<DevToolsViewerSettings.Fragment>(
                    "retry-source",
                    retryStore,
                    new StateSourceOptions<DevToolsViewerSettings.Fragment>
                    {
                        Writer = retryStore,
                        Watcher = retryStore,
                    }
                ),
            ]),
            writePlan: new StateWritePlan(
                null,
                new Dictionary<string, SourceId>(StringComparer.Ordinal)
                {
                    ["Theme"] = SourceId.From("theme-source"),
                    ["RetryCount"] = SourceId.From("retry-source"),
                }
            )
        );

        var builder = new ConfiglueBuilder();
        builder.Add<DevToolsViewerSettings>(model =>
            model.Sources(sources => sources.Add(composite.CreateSource("combined")))
        );
        await using var context = builder.CreateContext();
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        var node = JsonNode.Parse(session.SessionStartDocument.Json)!.AsObject();
        node["Theme"] = "Light";
        node["RetryCount"] = 9;
        var sync = await session.SyncDraftAsync(node.ToJsonString());
        sync.Success.ShouldBeTrue();

        var save = await session.CommitAsync();
        save.Committed.ShouldBeTrue();
        save.Receipt.ShouldNotBeNull();
        save.Receipt!.Sources.Count.ShouldBe(2);

        var value = await state.GetValueAsync();
        value.Theme.ShouldBe("Light");
        value.RetryCount.ShouldBe(9);

        // No flattening: each source received only its routed member.
        var themeFragment = (
            await themeStore.ReadAsync(Configlue.Resources.ConfiglueResourceContext.Default)
        ).Value!;
        themeFragment.Theme.Value.ShouldBe("Light");
        themeFragment.RetryCount.IsPresent.ShouldBeFalse();

        var retryFragment = (
            await retryStore.ReadAsync(Configlue.Resources.ConfiglueResourceContext.Default)
        ).Value!;
        retryFragment.RetryCount.Value.ShouldBe(9);
        retryFragment.Theme.IsPresent.ShouldBeFalse();
    }

    // DevTools-side representative for upstream/rebase behavior: clean adoption and
    // dirty-draft preservation state machines are owned by core EditSession tests.
    [Test]
    public async Task DirtySession_RebasesUpstreamWithoutLosingDraft()
    {
        var store = new InMemoryStateSource<DevToolsViewerSettings.Fragment>(
            new DevToolsViewerSettings.Fragment
            {
                Theme = Optional<string>.Present("Dark"),
                Notes = Optional<string?>.Present("v1"),
            }
        );
        await using var context = ViewerFixtures.CreateWatchedContext(store);
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        var draft = session.SessionStartDocument.Json.Replace("\"Dark\"", "\"Light\"");
        (await session.SyncDraftAsync(draft)).Success.ShouldBeTrue();

        store.Set(
            new DevToolsViewerSettings.Fragment
            {
                Theme = Optional<string>.Present("Dark"),
                Notes = Optional<string?>.Present("v2"),
            }
        );
        await ViewerFixtures.PollForUpstreamAsync(session);

        var rebased = await session.RebaseAsync();
        rebased.Success.ShouldBeTrue();
        session.HasUpstreamChanges.ShouldBeFalse();
        session.HasLocalChanges.ShouldBeTrue();

        var save = await session.CommitAsync();
        save.Committed.ShouldBeTrue();
        var value = await state.GetValueAsync();
        value.Theme.ShouldBe("Light");
        value.Notes.ShouldBe("v2");
    }

    [Test]
    public async Task Commit_ConflictSurfacesCategory()
    {
        var store = new InMemoryStateSource<DevToolsViewerSettings.Fragment>(
            new DevToolsViewerSettings.Fragment { Theme = Optional<string>.Present("Dark") }
        );
        await using var context = ViewerFixtures.CreateWatchedContext(store);
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        var draft = session.SessionStartDocument.Json.Replace("\"Dark\"", "\"Light\"");
        (await session.SyncDraftAsync(draft)).Success.ShouldBeTrue();

        // A concurrent change to the same member: FailOnConflict is the default.
        store.Set(
            new DevToolsViewerSettings.Fragment { Theme = Optional<string>.Present("Brisk") }
        );

        var save = await session.CommitAsync();
        save.Committed.ShouldBeFalse();
        save.Category.ShouldBe(ConfiglueEditorFailureCategory.Conflict);
        save.Errors.ShouldNotBeEmpty();
    }

    [Test]
    public async Task ServerValidation_AuthoritativeOverBrowserSchema()
    {
        var builder = new ConfiglueBuilder();
        builder.Add<DevToolsViewerSettings>(model =>
        {
            model.AddValidator(new RetryCountLimitValidator(100));
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
            );
        });
        await using var context = builder.CreateContext();
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        // The browser-side JSON schema service is advisory: it carries the member
        // shape but cannot express this server rule.
        session.GetSchemaSetup().SchemaJson.ShouldContain("RetryCount");

        var node = JsonNode.Parse(session.SessionStartDocument.Json)!.AsObject();
        node["RetryCount"] = 1000;
        var sync = await session.SyncDraftAsync(node.ToJsonString());
        sync.Success.ShouldBeTrue();

        var preview = await session.ValidateAsync();
        preview.Success.ShouldBeFalse();
        preview.Category.ShouldBe(ConfiglueEditorFailureCategory.Validation);
        preview.Errors.ShouldContain(error => error.Contains("exceeds limit"));

        var save = await session.CommitAsync();
        save.Committed.ShouldBeFalse();
        save.Category.ShouldBe(ConfiglueEditorFailureCategory.Validation);
        (await state.GetValueAsync()).RetryCount.ShouldBe(5);
    }

    [Test]
    public async Task SaveAndDiscard_Lifecycle()
    {
        await using var context = ViewerFixtures.CreateViewerContext();
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        var draft = session.SessionStartDocument.Json.Replace("\"Dark\"", "\"Light\"");
        (await session.SyncDraftAsync(draft)).Success.ShouldBeTrue();
        session.HasLocalChanges.ShouldBeTrue();

        session.DiscardChanges();
        session.HasLocalChanges.ShouldBeFalse();
        session.ModifiedPaths.ShouldBeEmpty();
        session.BuildCanonicalDraftJson().ShouldBe(session.SessionStartDocument.Json);
        (await state.GetValueAsync()).Theme.ShouldBe("Dark");

        (await session.SyncDraftAsync(draft)).Success.ShouldBeTrue();
        (await session.CommitAsync()).Committed.ShouldBeTrue();
        (await state.GetValueAsync()).Theme.ShouldBe("Light");
    }

    [Test]
    public async Task Secret_PlaceholderSyncIsNoOpWithoutPersistence()
    {
        const string password = "editor-pw-7k";
        await using var context = ViewerFixtures.CreateSecretViewerContext(password);
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        session.SessionStartDocument.Json.ShouldContain(ConfiglueSecrets.RedactedText);
        session.SessionStartDocument.Json.ShouldNotContain(password);

        var sync = await session.SyncDraftAsync(session.SessionStartDocument.Json);
        sync.Success.ShouldBeTrue();
        sync.ModifiedCount.ShouldBe(0);
        session.HasLocalChanges.ShouldBeFalse();

        var save = await session.CommitAsync();
        save.Committed.ShouldBeTrue();
        save.Receipt!.Sources.ShouldBeEmpty();
        (await state.GetValueAsync()).Database!.Password.ShouldBe(password);
    }

    [Test]
    public async Task Secret_PlaintextInDraftRejected()
    {
        const string password = "editor-pw-8m";
        await using var context = ViewerFixtures.CreateSecretViewerContext(password);
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        var draft = session.SessionStartDocument.Json.Replace(
            $"\"{ConfiglueSecrets.RedactedText}\"",
            "\"hunter2\""
        );
        var result = await session.SyncDraftAsync(draft);
        result.Success.ShouldBeFalse();
        result.Category.ShouldBe(ConfiglueEditorFailureCategory.Secret);
        session.HasLocalChanges.ShouldBeFalse();
        (await state.GetValueAsync()).Database!.Password.ShouldBe(password);
    }

    [Test]
    public async Task Secret_DeletedPlaceholderRejected()
    {
        const string password = "editor-pw-9n";
        await using var context = ViewerFixtures.CreateSecretViewerContext(password);
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        var draft = session.SessionStartDocument.Json.Replace(
            $",\n    \"Password\": \"{ConfiglueSecrets.RedactedText}\"",
            string.Empty
        );
        draft.ShouldNotContain("Password");

        var result = await session.SyncDraftAsync(draft);
        result.Success.ShouldBeFalse();
        result.Category.ShouldBe(ConfiglueEditorFailureCategory.NonEditable);
        session.HasLocalChanges.ShouldBeFalse();
        (await state.GetValueAsync()).Database!.Password.ShouldBe(password);
    }

    [Test]
    public async Task Secret_ExplicitChangeFlow()
    {
        const string password = "editor-pw-2q";
        await using var context = ViewerFixtures.CreateSecretViewerContext(password);
        var state = context.GetState<DevToolsViewerSettings>();
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        // Empty means unchanged.
        var noop = await session.ApplySecretAsync("Database.Password", string.Empty);
        noop.Success.ShouldBeTrue();
        session.HasLocalChanges.ShouldBeFalse();

        // Non-secret members belong in the Monaco draft, not the secret flow.
        var misrouted = await session.ApplySecretAsync("Database.Host", "db.new");
        misrouted.Success.ShouldBeFalse();
        misrouted.Category.ShouldBe(ConfiglueEditorFailureCategory.Schema);

        // The placeholder itself is never a valid secret value.
        var placeholder = await session.ApplySecretAsync(
            "Database.Password",
            ConfiglueSecrets.RedactedText
        );
        placeholder.Success.ShouldBeFalse();
        placeholder.Category.ShouldBe(ConfiglueEditorFailureCategory.Secret);

        var applied = await session.ApplySecretAsync("Database.Password", "rotated-pw");
        applied.Success.ShouldBeTrue();
        applied.ModifiedCount.ShouldBe(1);
        session.HasLocalChanges.ShouldBeTrue();

        var save = await session.CommitAsync();
        save.Committed.ShouldBeTrue();
        (await state.GetValueAsync()).Database!.Password.ShouldBe("rotated-pw");
        session.SessionStartDocument.Json.ShouldNotContain("rotated-pw");
    }

    [Test]
    public async Task Session_DisposesAndInvalidates()
    {
        await using var context = ViewerFixtures.CreateViewerContext();
        var state = context.GetState<DevToolsViewerSettings>();
        var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(state);
        session.StateIdentity.ShouldBe("devtools-viewer");
        session.IsDisposed.ShouldBeFalse();
        session.Dispose();
        session.IsDisposed.ShouldBeTrue();

        (await session.SyncDraftAsync(session.SessionStartDocument.Json)).Category.ShouldBe(
            ConfiglueEditorFailureCategory.Invalidated
        );
        (await session.CommitAsync()).Category.ShouldBe(ConfiglueEditorFailureCategory.Invalidated);
        (await session.ValidateAsync()).Category.ShouldBe(
            ConfiglueEditorFailureCategory.Invalidated
        );
        (await session.ApplySecretAsync("Database.Password", "x")).Category.ShouldBe(
            ConfiglueEditorFailureCategory.Invalidated
        );
        Should.Throw<ObjectDisposedException>(() => session.DiscardChanges());
        Should.Throw<ObjectDisposedException>(() => session.BuildCanonicalDraftJson());
        session.Dispose();
    }

    [Test]
    public async Task Session_PreservesStateIdentity()
    {
        var builder = new ConfiglueBuilder();
        builder.Add<DevToolsViewerSettings>(model =>
        {
            model.StateName = "named";
            model.Sources(sources =>
                sources.Add(
                    DevToolsFixtures.MemorySource(
                        new DevToolsViewerSettings.Fragment
                        {
                            Theme = Optional<string>.Present("Dark"),
                        }
                    )
                )
            );
        });
        await using var context = builder.CreateContext();
        var state = context.GetState<DevToolsViewerSettings>("named");
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state,
            stateName: "named"
        );

        session.StateIdentity.ShouldBe("devtools-viewer:named");
        session.StateName.ShouldBe("named");
    }

    [Test]
    public async Task Validate_WithoutPreviewSurfacesUnavailability()
    {
        await using var context = ViewerFixtures.CreateViewerContext();
        var inner = context.GetState<DevToolsViewerSettings>();
        var state = new NonPreviewState<DevToolsViewerSettings>(inner);
        using var session = await ConfiglueDevToolsEditorSession<DevToolsViewerSettings>.OpenAsync(
            state
        );

        var draft = session.SessionStartDocument.Json.Replace("\"Dark\"", "\"Light\"");
        (await session.SyncDraftAsync(draft)).Success.ShouldBeTrue();

        var preview = await session.ValidateAsync();
        preview.Success.ShouldBeTrue();
        preview.PreviewAvailable.ShouldBeFalse();

        // Commit still flows through normal routing.
        (await session.CommitAsync()).Committed.ShouldBeTrue();
        (await inner.GetValueAsync()).Theme.ShouldBe("Light");
    }

    [Test]
    public void Classify_MapsExceptionCategories()
    {
        ConfiglueDevToolsEditorSession<DevToolsViewerSettings>
            .Classify(new StateConflictException("stale"))
            .ShouldBe(ConfiglueEditorFailureCategory.Conflict);

        ConfiglueDevToolsEditorSession<DevToolsViewerSettings>
            .Classify(
                new ConfiglueValidationException("s", typeof(DevToolsViewerSettings), ["bad"])
            )
            .ShouldBe(ConfiglueEditorFailureCategory.Validation);

        ConfiglueDevToolsEditorSession<DevToolsViewerSettings>
            .Classify(
                new StateMultiWriteException(
                    StateWriteReceipt.Empty,
                    null,
                    [SourceId.From("a")],
                    [SourceId.From("b")],
                    new InvalidOperationException("boom")
                )
            )
            .ShouldBe(ConfiglueEditorFailureCategory.PartialWrite);

        ConfiglueDevToolsEditorSession<DevToolsViewerSettings>
            .Classify(new ObjectDisposedException("session"))
            .ShouldBe(ConfiglueEditorFailureCategory.Invalidated);

        ConfiglueDevToolsEditorSession<DevToolsViewerSettings>
            .Classify(
                new InvalidOperationException(
                    "This configure session is already saving, rebasing, or disposed."
                )
            )
            .ShouldBe(ConfiglueEditorFailureCategory.Invalidated);

        ConfiglueDevToolsEditorSession<DevToolsViewerSettings>
            .Classify(
                new InvalidOperationException(
                    "No writable state source is registered for this model."
                )
            )
            .ShouldBe(ConfiglueEditorFailureCategory.Routing);

        ConfiglueDevToolsEditorSession<DevToolsViewerSettings>
            .Classify(new System.Text.Json.JsonException("bad"))
            .ShouldBe(ConfiglueEditorFailureCategory.Parse);
    }

    private sealed class RetryCountLimitValidator(int max)
        : IConfiglueValidator<DevToolsViewerSettings>
    {
        public IReadOnlyList<string> Validate(DevToolsViewerSettings value) =>
            value.RetryCount > max ? [$"RetryCount {value.RetryCount} exceeds limit {max}."] : [];
    }

    private sealed class NonPreviewState<T>(IWritableState<T> inner)
        : IWritableState<T>,
            IConfiglueEditSessions<T>
    {
        public ValueTask<T> GetValueAsync(CancellationToken cancellationToken = default) =>
            inner.GetValueAsync(cancellationToken);

        public IDisposable OnChange(Action<T> listener) => inner.OnChange(listener);

        public ValueTask<StateWriteReceipt> SaveAsync(
            IConfiglueModelPatch<T> patch,
            CancellationToken cancellationToken = default
        ) => inner.SaveAsync(patch, cancellationToken);

        public ValueTask<EditSession<T>> OpenEditSessionAsync(
            CancellationToken cancellationToken = default
        ) => ((IConfiglueEditSessions<T>)inner).OpenEditSessionAsync(cancellationToken);

        public ValueTask<EditSession<T>> OpenEditSessionAsync(
            StateWritePlan writePlan,
            CancellationToken cancellationToken = default
        ) => ((IConfiglueEditSessions<T>)inner).OpenEditSessionAsync(writePlan, cancellationToken);
    }

    private static class ViewerFixtures
    {
        public static ConfiglueContext CreateViewerContext(
            string theme = "Dark",
            string? notes = null
        )
        {
            var builder = new ConfiglueBuilder();
            builder.Add<DevToolsViewerSettings>(model =>
                model.Sources(sources =>
                    sources.Add(
                        DevToolsFixtures.MemorySource(
                            new DevToolsViewerSettings.Fragment
                            {
                                Theme = Optional<string>.Present(theme),
                                RetryCount = Optional<int>.Present(5),
                                Notes = Optional<string?>.Present(notes),
                                Database = Optional<DevToolsViewerDatabase.Fragment?>.Present(
                                    new DevToolsViewerDatabase.Fragment
                                    {
                                        Host = Optional<string>.Present("db.local"),
                                        Port = Optional<int>.Present(5432),
                                    }
                                ),
                                Tags = Optional<List<string>>.Present(["web", "blue"]),
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

        public static ConfiglueContext CreateWatchedContext(
            InMemoryStateSource<DevToolsViewerSettings.Fragment> store
        )
        {
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
            return builder.CreateContext();
        }

        public static async Task PollForUpstreamAsync(
            ConfiglueDevToolsEditorSession<DevToolsViewerSettings> session
        )
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!session.HasUpstreamChanges)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException("The editor session did not observe upstream.");
                }

                await Task.Delay(50);
            }
        }
    }
}
