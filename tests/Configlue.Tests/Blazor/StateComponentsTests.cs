using System.Linq.Expressions;
using System.Security.Claims;
using Bunit;
using Configlue;
using Configlue.Hosting.Blazor;
using Configlue.Testing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Tests;

public sealed class StateComponentsTests
{
    [Test]
    public void Reader_InitialLoad_ResolvesSnapshotAndDetails()
    {
        using var ctx = new BunitContext();
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            Fragment("initial", retryCount: 5)
        );
        RegisterState(ctx, store);

        var (cut, state) = RenderReader(ctx);

        _ = cut;
        state.HasValue.ShouldBeTrue();
        state.IsLoading.ShouldBeFalse();
        state.Value.Label.ShouldBe("initial");
        var details = state.Snapshot!.GetDetails();
        details.Label.Value.ShouldBe("initial");
        details.RetryCount.Value.ShouldBe(5);
        details.RetryCount.IsEditable.ShouldBeTrue();
        details.RetryCount.Source.ShouldNotBeNull();
    }

    [Test]
    public void Reader_ReRendersOnStateChange()
    {
        using var ctx = new BunitContext();
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            Fragment("initial", retryCount: 3)
        );
        RegisterState(ctx, store);
        var (cut, state) = RenderReader(ctx);

        store.Set(Fragment("changed", retryCount: 9));

        cut.WaitForAssertion(() => state.Value.Label.ShouldBe("changed"));
        state.Value.RetryCount.ShouldBe(9);
    }

    [Test]
    public void Reader_ReloadFailure_PreservesLastSuccessfulValue()
    {
        using var ctx = new BunitContext();
        var state = new ControllableSnapshotState<AppSettings>(
            new StateSnapshot<AppSettings>(new AppSettings { Label = "good" }, null)
        );
        ctx.Services.AddSingleton<IReadOnlyState<AppSettings>>(state);
        var (cut, reader) = RenderReader(ctx);

        reader.Value.Label.ShouldBe("good");
        state.FailWith = new InvalidOperationException("reload failed");
        state.Raise(new AppSettings { Label = "bad" });

        cut.WaitForAssertion(() => reader.ReloadFailure.ShouldNotBeNull());
        reader.Value.Label.ShouldBe("good");
        reader.ReloadFailure!.Message.ShouldBe("reload failed");
        reader.LoadFailure.ShouldBeNull();
    }

    [Test]
    public void Reader_InitialLoadFailure_IsSurfaced()
    {
        using var ctx = new BunitContext();
        var state = new ControllableSnapshotState<AppSettings>(null!)
        {
            FailWith = new InvalidOperationException("cannot read"),
        };
        ctx.Services.AddSingleton<IReadOnlyState<AppSettings>>(state);
        Exception? failed = null;
        var cut = ctx.Render<StateReader<AppSettings>>(p =>
            p.Add(
                x => x.LoadFailedContent,
                (Exception e) =>
                {
                    failed = e;
                    return builder => builder.AddContent(0, e.Message);
                }
            )
        );

        cut.WaitForAssertion(() => failed.ShouldNotBeNull());
        failed!.Message.ShouldBe("cannot read");
        cut.Markup.ShouldContain("cannot read");
    }

    [Test]
    public void Reader_ReloadFailureDiagnosticsRaisesCallback()
    {
        using var ctx = new BunitContext();
        var state = new ControllableSnapshotState<AppSettings>(
            new StateSnapshot<AppSettings>(new AppSettings { Label = "good" }, null)
        );
        var diagnostics = new FakeReloadDiagnostics();
        ctx.Services.AddSingleton<IReadOnlyState<AppSettings>>(state);
        ctx.Services.AddSingleton<IConfiglueDiagnostics<AppSettings>>(diagnostics);
        Exception? callbackException = null;
        StateReaderContext<AppSettings>? captured = null;
        var cut = ctx.Render<StateReader<AppSettings>>(p =>
            p.Add(x => x.ChildContent, s => _ => captured = s)
                .Add(x => x.OnReloadFailed, (Exception e) => callbackException = e)
        );
        cut.WaitForAssertion(() => captured.ShouldNotBeNull());

        diagnostics.Raise(new InvalidOperationException("watcher reload"));

        cut.WaitForAssertion(() => callbackException.ShouldNotBeNull());
        callbackException!.Message.ShouldBe("watcher reload");
        captured!.ReloadFailure!.Message.ShouldBe("watcher reload");
        captured.Value.Label.ShouldBe("good");
    }

    [Test]
    public void Reader_SubjectChange_RebindsToNewValue()
    {
        using var ctx = new BunitContext();
        var state = new SwitchableSnapshotState(new AppSettings { Label = "subject-a" });
        ctx.Services.AddSingleton<IReadOnlyState<AppSettings>>(state);
        var (cut, reader) = RenderReader(ctx);
        reader.Value.Label.ShouldBe("subject-a");

        state.SwitchTo(new AppSettings { Label = "subject-b" });

        cut.WaitForAssertion(() => reader.Value.Label.ShouldBe("subject-b"));
    }

    [Test]
    public void Editor_InitialLoad_ExposesSessionAndDetails()
    {
        using var ctx = new BunitContext();
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            Fragment("initial", retryCount: 5)
        );
        RegisterState(ctx, store);

        var (cut, state) = RenderEditor(ctx);

        _ = cut;
        state.IsLoading.ShouldBeFalse();
        state.Value.Label.ShouldBe("initial");
        state.Session.ShouldNotBeNull();
        state.EditContext.ShouldNotBeNull();
        var details = state.SessionStart.GetDetails();
        details.RetryCount.Value.ShouldBe(5);
        details.RetryCount.IsEditable.ShouldBeTrue();
    }

    [Test]
    public void Editor_FieldEditing_TracksModifiedState()
    {
        using var ctx = new BunitContext();
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            Fragment("initial", retryCount: 3)
        );
        RegisterState(ctx, store);
        var (cut, state) = RenderEditorWithForm(ctx);

        state.EditContext.IsModified().ShouldBeFalse();
        cut.FindAll("input")[0].Change("edited");

        state.Value.Label.ShouldBe("edited");
        state.EditContext.IsModified().ShouldBeTrue();
    }

    [Test]
    public async Task Editor_SaveSuccess_CommitsAndMarksUnmodified()
    {
        using var ctx = new BunitContext();
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            Fragment("initial", retryCount: 3)
        );
        RegisterState(ctx, store);
        var (cut, state) = RenderEditor(ctx);
        state.Value.Label = "saved";

        await cut.InvokeAsync(() => state.SaveAsync().AsTask());

        state.IsSaving.ShouldBeFalse();
        state.LastReceipt.ShouldNotBeNull();
        state.EditContext.IsModified().ShouldBeFalse();
        var stored = (await store.ReadAsync()).Value!;
        stored.Label.Value.ShouldBe("saved");
    }

    [Test]
    public async Task Editor_ConfiglueValidationFailure_IsCategorized()
    {
        using var ctx = new BunitContext();
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            Fragment("initial", retryCount: 3)
        );
        RegisterState(ctx, store);
        StateEditorErrorEventArgs<AppSettings>? error = null;
        var (cut, state) = RenderEditor(ctx, configure: p => p.Add(x => x.OnError, e => error = e));
        state.Value.RetryCount = 200;

        await cut.InvokeAsync(() => state.SaveAsync().AsTask());

        cut.WaitForAssertion(() => error.ShouldNotBeNull());
        error!.Kind.ShouldBe(StateEditorErrorKind.Validation);
        error.ValidationException.ShouldNotBeNull();
        error.ValidationException!.Failures.ShouldNotBeEmpty();
        state.Value.RetryCount.ShouldBe(200);
    }

    [Test]
    public async Task Editor_BlazorValidationFailure_ReportsEditContextMessages()
    {
        using var ctx = new BunitContext();
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            Fragment("initial", retryCount: 3)
        );
        RegisterState(ctx, store);
        var (cut, state) = RenderEditorWithForm(ctx);

        cut.Find("input[type=number]").Change("200");

        var valid = await cut.InvokeAsync(() => state.EditContext.Validate());
        valid.ShouldBeFalse();
        state
            .EditContext.GetValidationMessages()
            .Any(message => message.Contains("RetryCount", StringComparison.Ordinal))
            .ShouldBeTrue();
    }

    [Test]
    public async Task Editor_Conflict_IsCategorized()
    {
        using var ctx = new BunitContext();
        var session = new EditSession<AppSettings>(
            new AppSettings { Label = "draft" },
            (_, _) => throw new StateConflictException("stale revision")
        );
        RegisterSessions(ctx, new StaticEditSessions<AppSettings>(session));
        StateEditorErrorEventArgs<AppSettings>? error = null;
        var (cut, state) = RenderEditor(ctx, configure: p => p.Add(x => x.OnError, e => error = e));

        await cut.InvokeAsync(() => state.SaveAsync().AsTask());

        cut.WaitForAssertion(() => error.ShouldNotBeNull());
        error!.Kind.ShouldBe(StateEditorErrorKind.Conflict);
        error.ConflictException.ShouldNotBeNull();
        error.ConflictException!.Message.ShouldBe("stale revision");
    }

    [Test]
    public async Task Editor_PartialWriteFailure_IsCategorized()
    {
        using var ctx = new BunitContext();
        var session = new EditSession<AppSettings>(
            new AppSettings { Label = "draft" },
            (_, _) =>
                throw new StateMultiWriteException(
                    StateWriteReceipt.Empty,
                    null,
                    [SourceId.From("a")],
                    [SourceId.From("b")],
                    new InvalidOperationException("partial")
                )
        );
        RegisterSessions(ctx, new StaticEditSessions<AppSettings>(session));
        StateEditorErrorEventArgs<AppSettings>? error = null;
        var (cut, state) = RenderEditor(ctx, configure: p => p.Add(x => x.OnError, e => error = e));

        await cut.InvokeAsync(() => state.SaveAsync().AsTask());

        cut.WaitForAssertion(() => error.ShouldNotBeNull());
        error!.Kind.ShouldBe(StateEditorErrorKind.MultiWrite);
        error.MultiWriteException.ShouldNotBeNull();
        error.MultiWriteException!.FailedSourceIds.ShouldContain(SourceId.From("a"));
        error.MultiWriteException.UnattemptedSourceIds.ShouldContain(SourceId.From("b"));
    }

    [Test]
    public void Editor_DirtyUpstreamChange_PreservesDraftAndNotifies()
    {
        using var ctx = new BunitContext();
        var upstream = new FakeUpstreamState<AppSettings>(new AppSettings { Label = "start" });
        var session = CreateUpstreamSession(new AppSettings { Label = "start" }, upstream);
        RegisterSessions(ctx, new StaticEditSessions<AppSettings>(session));
        var (cut, state) = RenderEditor(ctx);
        state.Value.Label = "mine";

        upstream.Raise(new AppSettings { Label = "upstream" });

        cut.WaitForAssertion(() => state.HasUpstreamChanges.ShouldBeTrue());
        state.Value.Label.ShouldBe("mine");
        state.HasLocalChanges.ShouldBeTrue();
    }

    [Test]
    public async Task Editor_ExplicitRebaseAsync_ReappliesDraft()
    {
        using var ctx = new BunitContext();
        var upstream = new FakeUpstreamState<AppSettings>(new AppSettings { Label = "start" });
        var session = CreateUpstreamSession(new AppSettings { Label = "start" }, upstream);
        RegisterSessions(ctx, new StaticEditSessions<AppSettings>(session));
        var (cut, state) = RenderEditor(ctx);
        state.Value.Label = "mine";
        upstream.Raise(new AppSettings { Label = "upstream" });
        cut.WaitForAssertion(() => state.HasUpstreamChanges.ShouldBeTrue());

        await cut.InvokeAsync(() => state.RebaseAsync().AsTask());

        state.Value.Label.ShouldBe("mine");
        state.HasUpstreamChanges.ShouldBeFalse();
        state.HasLocalChanges.ShouldBeTrue();
    }

    [Test]
    public void Editor_AuthenticationRegistrationInvalidatesThroughPublicDiHelper()
    {
        using var ctx = new BunitContext();
        var authenticationStateProvider = new TestAuthenticationStateProvider("user-a");
        ctx.Services.AddSingleton<AuthenticationStateProvider>(authenticationStateProvider);
        ctx.Services.AddBlazorAuthenticationConfiglueSubjectAccessor<EditorSubject>(
            (principal, _) =>
                ValueTaskCompat.FromResult(new EditorSubject(principal.FindFirst("user")!.Value))
        );
        var sessions = new SwitchingEditSessions<AppSettings>(() =>
            CreateUpstreamSession(
                new AppSettings { Label = "subject-a" },
                new FakeUpstreamState<AppSettings>(new AppSettings { Label = "subject-a" })
            )
        );
        ctx.Services.AddSingleton<IConfiglueEditSessions<AppSettings>>(sessions);
        var (cut, state) = RenderEditor(ctx);
        state.Value.Label.ShouldBe("subject-a");

        sessions.Current = () =>
            CreateUpstreamSession(
                new AppSettings { Label = "subject-b" },
                new FakeUpstreamState<AppSettings>(new AppSettings { Label = "subject-b" })
            );
        authenticationStateProvider.SetUser("user-b");

        cut.WaitForAssertion(() => state.Value.Label.ShouldBe("subject-b"));
    }

    [Test]
    public async Task Editor_SubjectChange_DirtyEditorPreservesDraftAndBlocksSave()
    {
        using var ctx = new BunitContext();
        var changeSource = new FakeSubjectChangeSource();
        var saveCalls = 0;
        var sessions = new SwitchingEditSessions<AppSettings>(() =>
            CreateUpstreamSession(
                new AppSettings { Label = "subject-a" },
                new FakeUpstreamState<AppSettings>(new AppSettings { Label = "subject-a" }),
                save: (_, _) =>
                {
                    Interlocked.Increment(ref saveCalls);
                    return ValueTask.FromResult(StateWriteReceipt.Empty);
                }
            )
        );
        ctx.Services.AddSingleton<IConfiglueEditSessions<AppSettings>>(sessions);
        ctx.Services.AddSingleton<IConfiglueSubjectChangeSource>(changeSource);
        StateEditorErrorEventArgs<AppSettings>? error = null;
        var (cut, state) = RenderEditor(ctx, configure: p => p.Add(x => x.OnError, e => error = e));
        state.Value.Label = "dirty-a";

        changeSource.Signal();
        cut.WaitForAssertion(() => state.IsSubjectChanged.ShouldBeTrue());
        state.Value.Label.ShouldBe("dirty-a");

        await cut.InvokeAsync(() => state.SaveAsync().AsTask());

        cut.WaitForAssertion(() => error.ShouldNotBeNull());
        error!.Kind.ShouldBe(StateEditorErrorKind.SubjectChanged);
        saveCalls.ShouldBe(0);
    }

    [Test]
    public void Editor_Dispose_UnsubscribesAndDisposesSession()
    {
        using var ctx = new BunitContext();
        var changeSource = new FakeSubjectChangeSource();
        var session = new EditSession<AppSettings>(
            new AppSettings { Label = "a" },
            (_, _) => ValueTask.FromResult(StateWriteReceipt.Empty)
        );
        RegisterSessions(ctx, new StaticEditSessions<AppSettings>(session));
        ctx.Services.AddSingleton<IConfiglueSubjectChangeSource>(changeSource);
        var (cut, state) = RenderEditor(ctx);
        changeSource.ListenerCount.ShouldBe(1);

        ((IDisposable)cut.Instance).Dispose();

        changeSource.ListenerCount.ShouldBe(0);
        state.Session.ShouldBeNull();
        var exception = Should.Throw<InvalidOperationException>(() => session.ResetToDefault());
        exception.Message.ShouldContain("disposed");
    }

    [Test]
    public async Task Editor_DisposeWhileSaving_AllowsCoreSaveToFinish()
    {
        using var ctx = new BunitContext();
        var saveStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var session = new EditSession<AppSettings>(
            new AppSettings { Label = "a" },
            async (_, _) =>
            {
                saveStarted.TrySetResult();
                await releaseSave.Task.ConfigureAwait(false);
                return StateWriteReceipt.Empty;
            }
        );
        RegisterSessions(ctx, new StaticEditSessions<AppSettings>(session));
        var (cut, state) = RenderEditor(ctx);
        state.Value.Label = "edited";

        Task? saveTask = null;
        await cut.InvokeAsync(() =>
        {
            saveTask = state.SaveAsync().AsTask();
            return Task.CompletedTask;
        });
        await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        ((IDisposable)cut.Instance).Dispose();
        var renderCount = cut.RenderCount;
        state.IsSaving.ShouldBeFalse();
        releaseSave.TrySetResult();
        await cut.InvokeAsync(() => saveTask!);

        session.IsCommitted.ShouldBeTrue();
        state.LastReceipt.ShouldBeNull();
        state.IsSaving.ShouldBeFalse();
        cut.RenderCount.ShouldBe(renderCount);
    }

    [Test]
    public async Task Editor_SaveFailureAfterSubjectInvalidation_DoesNotPublishStaleError()
    {
        using var ctx = new BunitContext();
        var subjects = new FakeSubjectChangeSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new EditSession<AppSettings>(
            new AppSettings { Label = "original" },
            async (_, _) =>
            {
                started.TrySetResult();
                await release.Task.ConfigureAwait(false);
                throw new InvalidOperationException("obsolete save failure");
            }
        );
        RegisterSessions(ctx, new StaticEditSessions<AppSettings>(session));
        ctx.Services.AddSingleton<IConfiglueSubjectChangeSource>(subjects);
        var (cut, state) = RenderEditor(ctx);
        state.Value.Label = "edited";
        Task? save = null;
        await cut.InvokeAsync(() =>
        {
            save = state.SaveAsync().AsTask();
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(subjects.Signal);
        state.IsSubjectChanged.ShouldBeTrue();
        release.SetResult();
        await cut.InvokeAsync(() => save!);
        state.LastError.ShouldBeNull();
        state.LastReceipt.ShouldBeNull();
        state.IsSaving.ShouldBeFalse();
        state.Value.Label.ShouldBe("edited");
    }

    [Test]
    public async Task Reader_ReloadRace_NewerCompletionWins()
    {
        using var ctx = new BunitContext();
        var state = new GatedSnapshotState<AppSettings>(
            new StateSnapshot<AppSettings>(new AppSettings { Label = "initial" }, null)
        );
        ctx.Services.AddSingleton<IReadOnlyState<AppSettings>>(state);
        var (cut, reader) = RenderReader(ctx);
        reader.Value.Label.ShouldBe("initial");

        var aStarted = new TaskCompletionSource();
        var bStarted = new TaskCompletionSource();
        var a = new TaskCompletionSource<StateSnapshot<AppSettings>>();
        var b = new TaskCompletionSource<StateSnapshot<AppSettings>>();
        state.Enqueue(() =>
        {
            aStarted.TrySetResult();
            return new ValueTask<StateSnapshot<AppSettings>>(a.Task);
        });
        state.Enqueue(() =>
        {
            bStarted.TrySetResult();
            return new ValueTask<StateSnapshot<AppSettings>>(b.Task);
        });

        await cut.InvokeAsync(() => state.Raise(new AppSettings { Label = "a" }));
        await aStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => state.Raise(new AppSettings { Label = "b" }));
        await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() =>
            b.SetResult(new StateSnapshot<AppSettings>(new AppSettings { Label = "b" }, null))
        );
        await cut.InvokeAsync(() => Task.CompletedTask);
        reader.Value.Label.ShouldBe("b");

        await cut.InvokeAsync(() =>
            a.SetResult(new StateSnapshot<AppSettings>(new AppSettings { Label = "a" }, null))
        );
        await cut.InvokeAsync(() => Task.CompletedTask);

        reader.Value.Label.ShouldBe("b");
    }

    [Test]
    public async Task Reader_StaleReloadFailure_DoesNotOverwriteNewerSuccess()
    {
        using var ctx = new BunitContext();
        var state = new GatedSnapshotState<AppSettings>(
            new StateSnapshot<AppSettings>(new AppSettings { Label = "initial" }, null)
        );
        ctx.Services.AddSingleton<IReadOnlyState<AppSettings>>(state);
        var (cut, reader) = RenderReader(ctx);
        reader.Value.Label.ShouldBe("initial");

        var a = new TaskCompletionSource<StateSnapshot<AppSettings>>();
        var b = new TaskCompletionSource<StateSnapshot<AppSettings>>();
        state.Enqueue(() => new ValueTask<StateSnapshot<AppSettings>>(a.Task));
        state.Enqueue(() => new ValueTask<StateSnapshot<AppSettings>>(b.Task));

        await cut.InvokeAsync(() => state.Raise(new AppSettings { Label = "a" }));
        await cut.InvokeAsync(() => state.Raise(new AppSettings { Label = "b" }));

        await cut.InvokeAsync(() =>
            b.SetResult(new StateSnapshot<AppSettings>(new AppSettings { Label = "b" }, null))
        );
        await cut.InvokeAsync(() => Task.CompletedTask);
        reader.Value.Label.ShouldBe("b");
        reader.ReloadFailure.ShouldBeNull();

        await cut.InvokeAsync(() => a.SetException(new InvalidOperationException("stale reload")));
        await cut.InvokeAsync(() => Task.CompletedTask);

        reader.Value.Label.ShouldBe("b");
        reader.ReloadFailure.ShouldBeNull();
    }

    [Test]
    public async Task Reader_DisposeWhileReloadInFlight_SuppressesPublication()
    {
        using var ctx = new BunitContext();
        var state = new GatedSnapshotState<AppSettings>(
            new StateSnapshot<AppSettings>(new AppSettings { Label = "initial" }, null)
        );
        ctx.Services.AddSingleton<IReadOnlyState<AppSettings>>(state);
        var (cut, reader) = RenderReader(ctx);
        reader.Value.Label.ShouldBe("initial");

        var reload = new TaskCompletionSource<StateSnapshot<AppSettings>>();
        state.Enqueue(() => new ValueTask<StateSnapshot<AppSettings>>(reload.Task));

        await cut.InvokeAsync(() => state.Raise(new AppSettings { Label = "late" }));

        var renderCount = cut.RenderCount;
        ((IDisposable)cut.Instance).Dispose();
        await cut.InvokeAsync(() =>
            reload.SetResult(
                new StateSnapshot<AppSettings>(new AppSettings { Label = "late" }, null)
            )
        );
        await cut.InvokeAsync(() => Task.CompletedTask);

        reader.Value.Label.ShouldBe("initial");
        cut.RenderCount.ShouldBe(renderCount);
    }

    [Test]
    public async Task Editor_RapidSubjectChanges_SettleOnLatestSubject()
    {
        using var ctx = new BunitContext();
        var changeSource = new FakeSubjectChangeSource();
        var bSession = CreateUpstreamSession(
            new AppSettings { Label = "subject-b" },
            new FakeUpstreamState<AppSettings>(new AppSettings { Label = "subject-b" })
        );
        var cSession = CreateUpstreamSession(
            new AppSettings { Label = "subject-c" },
            new FakeUpstreamState<AppSettings>(new AppSettings { Label = "subject-c" })
        );
        var bStarted = new TaskCompletionSource();
        var b = new TaskCompletionSource<EditSession<AppSettings>>();
        var sessions = new GatedEditSessions<AppSettings>(call =>
            call switch
            {
                0 => ValueTask.FromResult(
                    CreateUpstreamSession(
                        new AppSettings { Label = "subject-a" },
                        new FakeUpstreamState<AppSettings>(new AppSettings { Label = "subject-a" })
                    )
                ),
                1 => OpenBlocked(bStarted, b),
                2 => ValueTask.FromResult(cSession),
                _ => throw new InvalidOperationException("unexpected open"),
            }
        );
        ctx.Services.AddSingleton<IConfiglueEditSessions<AppSettings>>(sessions);
        ctx.Services.AddSingleton<IConfiglueSubjectChangeSource>(changeSource);
        var (cut, state) = RenderEditor(ctx);
        state.Value.Label.ShouldBe("subject-a");

        await cut.InvokeAsync(() =>
        {
            changeSource.Signal();
            return Task.CompletedTask;
        });
        await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        state.IsLoading.ShouldBeTrue();

        await cut.InvokeAsync(() =>
        {
            changeSource.Signal();
            return Task.CompletedTask;
        });
        await cut.InvokeAsync(() => b.SetResult(bSession));
        await cut.InvokeAsync(() => Task.CompletedTask);

        state.Value.Label.ShouldBe("subject-c");
        state.IsSubjectChanged.ShouldBeFalse();
        state.IsLoading.ShouldBeFalse();

        ((IDisposable)cut.Instance).Dispose();
        Should.Throw<InvalidOperationException>(() => bSession.ResetToDefault());
    }

    [Test]
    public async Task Editor_DisposeWhileOpenInFlight_DisposesStaleSession()
    {
        using var ctx = new BunitContext();
        var changeSource = new FakeSubjectChangeSource();
        var bSession = CreateUpstreamSession(
            new AppSettings { Label = "subject-b" },
            new FakeUpstreamState<AppSettings>(new AppSettings { Label = "subject-b" })
        );
        var bStarted = new TaskCompletionSource();
        var b = new TaskCompletionSource<EditSession<AppSettings>>();
        var sessions = new GatedEditSessions<AppSettings>(call =>
            call switch
            {
                0 => ValueTask.FromResult(
                    CreateUpstreamSession(
                        new AppSettings { Label = "subject-a" },
                        new FakeUpstreamState<AppSettings>(new AppSettings { Label = "subject-a" })
                    )
                ),
                1 => OpenBlocked(bStarted, b),
                _ => throw new InvalidOperationException("unexpected open"),
            }
        );
        ctx.Services.AddSingleton<IConfiglueEditSessions<AppSettings>>(sessions);
        ctx.Services.AddSingleton<IConfiglueSubjectChangeSource>(changeSource);
        var (cut, state) = RenderEditor(ctx);
        state.Value.Label.ShouldBe("subject-a");

        await cut.InvokeAsync(() =>
        {
            changeSource.Signal();
            return Task.CompletedTask;
        });
        await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        ((IDisposable)cut.Instance).Dispose();
        await cut.InvokeAsync(() => b.SetResult(bSession));
        await cut.InvokeAsync(() => Task.CompletedTask);

        state.Session.ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => bSession.ResetToDefault());
    }

    private static ValueTask<EditSession<AppSettings>> OpenBlocked(
        TaskCompletionSource started,
        TaskCompletionSource<EditSession<AppSettings>> completion
    )
    {
        started.TrySetResult();
        return new ValueTask<EditSession<AppSettings>>(completion.Task);
    }

    private static (
        IRenderedComponent<StateReader<AppSettings>> Cut,
        StateReaderContext<AppSettings> Context
    ) RenderReader(BunitContext ctx)
    {
        StateReaderContext<AppSettings>? captured = null;
        var cut = ctx.Render<StateReader<AppSettings>>(p =>
            p.Add(x => x.ChildContent, state => _ => captured = state)
        );
        cut.WaitForAssertion(() => captured.ShouldNotBeNull());
        return (cut, captured!);
    }

    private static (
        IRenderedComponent<StateEditor<AppSettings>> Cut,
        StateEditorContext<AppSettings> Context
    ) RenderEditor(
        BunitContext ctx,
        Action<ComponentParameterCollectionBuilder<StateEditor<AppSettings>>>? configure = null
    )
    {
        StateEditorContext<AppSettings>? captured = null;
        var cut = ctx.Render<StateEditor<AppSettings>>(parameters =>
        {
            configure?.Invoke(parameters);
            parameters.Add(x => x.ChildContent, state => _ => captured = state);
        });
        cut.WaitForAssertion(() => captured.ShouldNotBeNull());
        return (cut, captured!);
    }

    private static (
        IRenderedComponent<StateEditor<AppSettings>> Cut,
        StateEditorContext<AppSettings> Context
    ) RenderEditorWithForm(BunitContext ctx)
    {
        StateEditorContext<AppSettings>? captured = null;
        var cut = ctx.Render<StateEditor<AppSettings>>(p =>
            p.Add(
                x => x.ChildContent,
                state =>
                {
                    captured = state;
                    return builder =>
                    {
                        builder.OpenComponent<SettingsForm>(0);
                        builder.AddAttribute(1, nameof(SettingsForm.Context), state);
                        builder.CloseComponent();
                    };
                }
            )
        );
        cut.WaitForAssertion(() => captured.ShouldNotBeNull());
        return (cut, captured!);
    }

    private static void RegisterState(
        BunitContext ctx,
        InMemoryStateSource<AppSettings.Fragment> store,
        StateWritePlan? writePlan = null
    ) =>
        ctx.Services.AddConfiglueState<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("settings", store, new StateSourceOptions<AppSettings.Fragment> { Writer = store, Watcher = store }),
            ]),
            writePlan: writePlan,
            onChangeDebounce: TimeSpan.Zero
        );

    private static void RegisterSessions(
        BunitContext ctx,
        IConfiglueEditSessions<AppSettings> sessions
    ) => ctx.Services.AddSingleton(sessions);

    private static EditSession<AppSettings> CreateUpstreamSession(
        AppSettings value,
        FakeUpstreamState<AppSettings> upstream,
        AppSettings? defaultValue = null,
        Func<AppSettings, CancellationToken, ValueTask<StateWriteReceipt>>? save = null
    ) =>
        new(
            Clone(value),
            new StateSnapshot<AppSettings>(Clone(value), null),
            WrapSave(save),
            static (baseline, desired, current) =>
                string.Equals(desired.Label, baseline.Label, StringComparison.Ordinal)
                    ? current
                    : desired,
            static (current, baseline) =>
                !string.Equals(current.Label, baseline.Label, StringComparison.Ordinal),
            _ =>
                ValueTask.FromResult(new StateSnapshot<AppSettings>(Clone(upstream.Current), null)),
            Clone,
            defaultValue ?? new AppSettings { Label = "default" },
            upstream
        );

    private static Func<
        AppSettings,
        CancellationToken,
        ValueTask<StateCommitResult<AppSettings>>
    > WrapSave(Func<AppSettings, CancellationToken, ValueTask<StateWriteReceipt>>? save) =>
        save is null
            ? static (value, _) =>
                ValueTask.FromResult(
                    new StateCommitResult<AppSettings>(
                        StateWriteReceipt.Empty,
                        new StateSnapshot<AppSettings>(value, null)
                    )
                )
            : async (value, token) =>
                new StateCommitResult<AppSettings>(
                    await save(value, token).ConfigureAwait(false),
                    new StateSnapshot<AppSettings>(value, null)
                );

    private static AppSettings Clone(AppSettings value) => value.DeepClone();

    private static AppSettings.Fragment Fragment(
        string? label,
        int retryCount = 3,
        bool enabled = true
    ) =>
        new()
        {
            Label = Optional<string?>.Present(label),
            RetryCount = Optional<int>.Present(retryCount),
            Enabled = Optional<bool>.Present(enabled),
        };

    private sealed class SettingsForm : ComponentBase
    {
        [Parameter]
        public StateEditorContext<AppSettings> Context { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.OpenComponent<EditForm>(0);
            builder.AddAttribute(1, nameof(EditForm.EditContext), Context.EditContext);
            builder.AddAttribute(
                2,
                "ChildContent",
                (RenderFragment<EditContext>)(
                    _ =>
                        form =>
                        {
                            form.OpenComponent<DataAnnotationsValidator>(0);
                            form.CloseComponent();
                            form.OpenComponent<InputText>(1);
                            form.AddAttribute(2, nameof(InputText.Value), Context.Value.Label);
                            form.AddAttribute(
                                3,
                                nameof(InputText.ValueChanged),
                                EventCallback.Factory.Create<string?>(
                                    this,
                                    value => Context.Value.Label = value
                                )
                            );
                            form.AddAttribute(
                                4,
                                nameof(InputText.ValueExpression),
                                (Expression<Func<string?>>)(() => Context.Value.Label)
                            );
                            form.CloseComponent();
                            form.OpenComponent<InputNumber<int>>(5);
                            form.AddAttribute(
                                6,
                                nameof(InputNumber<int>.Value),
                                Context.Value.RetryCount
                            );
                            form.AddAttribute(
                                7,
                                nameof(InputNumber<int>.ValueChanged),
                                EventCallback.Factory.Create<int>(
                                    this,
                                    value => Context.Value.RetryCount = value
                                )
                            );
                            form.AddAttribute(
                                8,
                                nameof(InputNumber<int>.ValueExpression),
                                (Expression<Func<int>>)(() => Context.Value.RetryCount)
                            );
                            form.CloseComponent();
                        }
                )
            );
            builder.CloseComponent();
        }
    }

    private sealed class ControllableSnapshotState<T>
        : IReadOnlyState<T>,
            IConfiglueStateSnapshotRuntime<T>
    {
        private readonly List<Action<T>> _listeners = [];
        private StateSnapshot<T> _snapshot;

        public ControllableSnapshotState(StateSnapshot<T> snapshot) => _snapshot = snapshot;

        public Exception? FailWith { get; set; }

        public int ListenerCount => _listeners.Count;

        public IDisposable OnChange(Action<T> listener)
        {
            ArgumentNullException.ThrowIfNull(listener);
            _listeners.Add(listener);
            return new ActionDisposable(() => _listeners.Remove(listener));
        }

        public ValueTask<T> GetValueAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_snapshot.Value);
        }

        public ValueTask<StateSnapshot<T>> GetSnapshotAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailWith is not null)
            {
                throw FailWith;
            }

            return ValueTask.FromResult(_snapshot);
        }

        public void Raise(T value)
        {
            foreach (var listener in _listeners.ToArray())
            {
                listener(value);
            }
        }
    }

    private sealed class SwitchableSnapshotState
        : IReadOnlyState<AppSettings>,
            IConfiglueStateSnapshotRuntime<AppSettings>
    {
        private AppSettings _current;
        private Action<AppSettings>? _listener;

        public SwitchableSnapshotState(AppSettings current) => _current = current;

        public IDisposable OnChange(Action<AppSettings> listener)
        {
            _listener = listener;
            return new ActionDisposable(() => _listener = null);
        }

        public ValueTask<AppSettings> GetValueAsync(
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(_current);

        public ValueTask<StateSnapshot<AppSettings>> GetSnapshotAsync(
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(new StateSnapshot<AppSettings>(_current, null));

        public void SwitchTo(AppSettings value)
        {
            _current = value;
            _listener?.Invoke(value);
        }
    }

    private sealed class GatedSnapshotState<T>
        : IReadOnlyState<T>,
            IConfiglueStateSnapshotRuntime<T>
    {
        private readonly List<Action<T>> _listeners = [];
        private readonly Queue<Func<ValueTask<StateSnapshot<T>>>> _pending = new();
        private readonly StateSnapshot<T> _immediate;

        public GatedSnapshotState(StateSnapshot<T> immediate) => _immediate = immediate;

        public IDisposable OnChange(Action<T> listener)
        {
            ArgumentNullException.ThrowIfNull(listener);
            _listeners.Add(listener);
            return new ActionDisposable(() => _listeners.Remove(listener));
        }

        public ValueTask<T> GetValueAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_immediate.Value);
        }

        public ValueTask<StateSnapshot<T>> GetSnapshotAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _pending.Count > 0 ? _pending.Dequeue()() : ValueTask.FromResult(_immediate);
        }

        public void Enqueue(Func<ValueTask<StateSnapshot<T>>> next)
        {
            ArgumentNullException.ThrowIfNull(next);
            _pending.Enqueue(next);
        }

        public void Raise(T value)
        {
            foreach (var listener in _listeners.ToArray())
            {
                listener(value);
            }
        }
    }

    private sealed class FakeReloadDiagnostics : IConfiglueReloadFailureDiagnostics<AppSettings>
    {
        private Action<Exception>? _listener;

        public ConfiglueStateDiagnostics GetDiagnostics() => throw new NotSupportedException();

        public ConfiglueCheckOperation Check(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IDisposable OnReloadFailed(Action<Exception> listener)
        {
            _listener = listener;
            return new ActionDisposable(() => _listener = null);
        }

        public void Raise(Exception exception) => _listener?.Invoke(exception);
    }

    private sealed class FakeUpstreamState<T> : IReadOnlyState<T>
    {
        private Action<T>? _listener;

        public FakeUpstreamState(T current) => Current = current;

        public T Current { get; private set; }

        public IDisposable OnChange(Action<T> listener)
        {
            _listener = listener;
            return new ActionDisposable(() => _listener = null);
        }

        public ValueTask<T> GetValueAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Current);

        public void Raise(T value)
        {
            Current = value;
            _listener?.Invoke(value);
        }
    }

    private sealed class FakeSubjectChangeSource : IConfiglueSubjectChangeSource
    {
        private readonly List<Action> _listeners = [];

        public int ListenerCount => _listeners.Count;

        public IDisposable OnChange(Action listener)
        {
            _listeners.Add(listener);
            return new ActionDisposable(() => _listeners.Remove(listener));
        }

        public void Signal()
        {
            foreach (var listener in _listeners.ToArray())
            {
                listener();
            }
        }
    }

    private sealed class StaticEditSessions<T> : IConfiglueEditSessions<T>
        where T : class
    {
        private readonly EditSession<T> _session;

        public StaticEditSessions(EditSession<T> session) => _session = session;

        public ValueTask<EditSession<T>> OpenEditSessionAsync(
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(_session);

        public ValueTask<EditSession<T>> OpenEditSessionAsync(
            StateWritePlan writePlan,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(writePlan);
            return ValueTask.FromResult(_session);
        }
    }

    private sealed class SwitchingEditSessions<T> : IConfiglueEditSessions<T>
        where T : class
    {
        public SwitchingEditSessions(Func<EditSession<T>> current) => Current = current;

        public Func<EditSession<T>> Current { get; set; }

        public ValueTask<EditSession<T>> OpenEditSessionAsync(
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(Current());

        public ValueTask<EditSession<T>> OpenEditSessionAsync(
            StateWritePlan writePlan,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(writePlan);
            return ValueTask.FromResult(Current());
        }
    }

    private sealed class GatedEditSessions<T> : IConfiglueEditSessions<T>
        where T : class
    {
        private readonly Func<int, ValueTask<EditSession<T>>> _open;
        private int _calls;

        public GatedEditSessions(Func<int, ValueTask<EditSession<T>>> open) => _open = open;

        public int OpenCount => Volatile.Read(ref _calls);

        public ValueTask<EditSession<T>> OpenEditSessionAsync(
            CancellationToken cancellationToken = default
        )
        {
            var call = Interlocked.Increment(ref _calls) - 1;
            return _open(call);
        }

        public ValueTask<EditSession<T>> OpenEditSessionAsync(
            StateWritePlan writePlan,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(writePlan);
            return OpenEditSessionAsync(cancellationToken);
        }
    }

    private sealed class ActionDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    private sealed record EditorSubject(string UserId) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From(UserId);
    }

    private sealed class TestAuthenticationStateProvider(string userId)
        : AuthenticationStateProvider
    {
        private AuthenticationState _state = CreateState(userId);

        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(_state);

        public void SetUser(string nextUserId)
        {
            _state = CreateState(nextUserId);
            NotifyAuthenticationStateChanged(Task.FromResult(_state));
        }

        private static AuthenticationState CreateState(string userId) =>
            new(
                new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim("user", userId)], authenticationType: "test")
                )
            );
    }
}
