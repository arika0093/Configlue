using System.Collections.Concurrent;
using System.Security.Claims;
using System.Threading.Channels;
using Configlue.Sources;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;
#if !NET48
using Configlue.Hosting.AspNetCore;
using Configlue.Hosting.Blazor;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
#endif

namespace Configlue.Tests;

public sealed class PerSubjectDependencyInjectionTests
{
    [Test]
    public async Task ScopedOptionsResolveAndSaveUsingEachScopeCurrentSubject()
    {
        var users = new SubjectStateStore<AppSettings.Fragment>();
        var subjectA = new SettingsSubject("tenant-a", "user-a");
        var subjectB = new SettingsSubject("tenant-b", "user-b");
        users.Set(subjectA.Key, Fragment("user-a"));
        users.Set(subjectB.Key, Fragment("user-b"));

        var server = new InMemoryStateSource<DatabaseSettings.Fragment>(
            new DatabaseSettings.Fragment { Host = Optional<string>.Present("server.db") }
        );
        var services = new ServiceCollection();
        services.AddScoped<MutableSubjectAccessor>();
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.PerSubject<MutableSubjectAccessor>();
                model.WritePlan = StateWritePlan.DefaultTo(SourceId.From("users"));
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "users",
                            users,
                            writer: users,
                            watcher: users,
                            resourceKeySelector: subject =>
                                subject is SettingsSubject typed
                                    ? ResourceKey.From(typed.Key)
                                    : ResourceKey.Default
                        )
                    )
                );
            });
            builder.Add<DatabaseSettings>(model =>
                model.Sources(sources =>
                    sources.Add(new StateSource<DatabaseSettings.Fragment>("server", server))
                )
            );
        });

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }
        );
        var sharedSubjectOptions = provider.GetRequiredService<ISubjectState<AppSettings>>();
        Should.Throw<InvalidOperationException>(() =>
            provider.GetRequiredService<IConfiglueInspection<AppSettings>>()
        );
        Should.Throw<InvalidOperationException>(() =>
            provider.GetRequiredService<IConfiglueEditSessions<AppSettings>>()
        );
        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();
        var accessorA = scopeA.ServiceProvider.GetRequiredService<MutableSubjectAccessor>();
        var accessorB = scopeB.ServiceProvider.GetRequiredService<MutableSubjectAccessor>();
        accessorA.Set(subjectA);
        accessorB.Set(subjectB);

        var readA = scopeA.ServiceProvider.GetRequiredService<IReadOnlyState<AppSettings>>();
        var writeA = scopeA.ServiceProvider.GetRequiredService<IWritableState<AppSettings>>();
        var readB = scopeB.ServiceProvider.GetRequiredService<IReadOnlyState<AppSettings>>();
        var writeB = scopeB.ServiceProvider.GetRequiredService<IWritableState<AppSettings>>();

        ReferenceEquals(readA, writeA).ShouldBeTrue();
        ReferenceEquals(readB, writeB).ShouldBeTrue();
        ReferenceEquals(readA, readB).ShouldBeFalse();
        var inspectionA = scopeA.ServiceProvider.GetRequiredService<
            IConfiglueInspection<AppSettings>
        >();
        var sessionsA = scopeA.ServiceProvider.GetRequiredService<
            IConfiglueEditSessions<AppSettings>
        >();
        ReferenceEquals(readA, inspectionA).ShouldBeTrue();
        ReferenceEquals(readA, sessionsA).ShouldBeTrue();
        ReferenceEquals(
                sharedSubjectOptions,
                provider.GetRequiredService<ISubjectState<AppSettings>>()
            )
            .ShouldBeTrue();
        (await readA.GetValueAsync()).Label.ShouldBe("user-a");
        (await readB.GetValueAsync()).Label.ShouldBe("user-b");

        await writeA.SaveAsync(
            new AppSettings.Patch { Label = FragmentOperation<string?>.Set("saved-a") }
        );
        await writeB.SaveAsync(
            new AppSettings.Patch { Label = FragmentOperation<string?>.Set("saved-b") }
        );
        users.Read(subjectA.Key).Value!.Label.Value.ShouldBe("saved-a");
        users.Read(subjectB.Key).Value!.Label.Value.ShouldBe("saved-b");

        var serverOptionsA = scopeA.ServiceProvider.GetRequiredService<
            IReadOnlyState<DatabaseSettings>
        >();
        var serverOptionsB = scopeB.ServiceProvider.GetRequiredService<
            IReadOnlyState<DatabaseSettings>
        >();
        ReferenceEquals(serverOptionsA, serverOptionsB).ShouldBeTrue();
        (await serverOptionsA.GetValueAsync()).Host.ShouldBe("server.db");

        (await sharedSubjectOptions.ForSubject(subjectB).GetValueAsync()).Label.ShouldBe("saved-b");

        var detailsA = await readA.GetDetailsAsync();
        var resolutionA = detailsA.Label.Source?.Resolution;
        resolutionA.ShouldNotBeNull();
        (resolutionA!.LogicalSubjectKey).ShouldBe(subjectA.Key);
        (resolutionA.ResourceKey).ShouldBe(ResourceKey.From(subjectA.Key));
        (resolutionA.Route).ShouldBe(RouteKey.Default);
    }

    [Test]
    public async Task ScopedChangeSubscriptionRebindsAfterSubjectInvalidation()
    {
        var users = new SubjectStateStore<AppSettings.Fragment>();
        var subjectA = new SettingsSubject("tenant", "user-a");
        var subjectB = new SettingsSubject("tenant", "user-b");
        users.Set(subjectA.Key, Fragment("before-a"));
        users.Set(subjectB.Key, Fragment("before-b"));
        var services = new ServiceCollection();
        services.AddScoped<MutableSubjectAccessor>();
        services.AddConfiglue(builder =>
            builder.Add<AppSettings>(model =>
            {
                model.PerSubject<MutableSubjectAccessor>();
                model.OnChangeDebounce = TimeSpan.Zero;
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "users",
                            users,
                            watcher: users,
                            resourceKeySelector: subject =>
                                subject is SettingsSubject typed
                                    ? ResourceKey.From(typed.Key)
                                    : ResourceKey.Default
                        )
                    )
                );
            })
        );

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<MutableSubjectAccessor>();
        accessor.Set(subjectA);
        var changed = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = scope
            .ServiceProvider.GetRequiredService<IReadOnlyState<AppSettings>>()
            .OnChange(value =>
            {
                if (value.Label == "after-b")
                {
                    changed.TrySetResult(value.Label);
                }
            });

        await Task.Delay(150);
        accessor.Set(subjectB);
        await Task.Delay(150);
        users.Set(subjectA.Key, Fragment("after-a"));
        await Task.Delay(150);
        changed.Task.IsCompleted.ShouldBeFalse();
        users.Set(subjectB.Key, Fragment("after-b"));
        (await changed.Task.WaitAsync(TimeSpan.FromSeconds(3))).ShouldBe("after-b");
    }

    [Test]
    public async Task SubjectInvalidationRebindsAcrossRoutesAndNotifiesEvenForEqualValues()
    {
        var users = new SubjectStateStore<AppSettings.Fragment>();
        var key = SubjectKey.FromSegments("same-subject-key");
        var routeA = RouteKey.From("backend-a");
        var routeB = RouteKey.From("backend-b");
        users.Set(key, routeA, Fragment("same"));
        users.Set(key, routeB, Fragment("same"));
        var services = new ServiceCollection();
        services.AddScoped<MutableRoutedSubjectAccessor>();
        services.AddConfiglue(builder =>
            builder.Add<AppSettings>(model =>
            {
                model.PerSubject<MutableRoutedSubjectAccessor>();
                model.OnChangeDebounce = TimeSpan.Zero;
                model.Sources(sources =>
                {
                    var routed = new StateSourceSetBuilder<AppSettings.Fragment>();
                    routed
                        .Add("users", users)
                        .ResourceKeyBy<RoutedSettingsSubject>(_ => ResourceKey.From(key))
                        .RouteBy<RoutedSettingsSubject>(static subject => subject.Route);
                    sources.Add(routed.Build().Sources[0]);
                });
            })
        );

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<MutableRoutedSubjectAccessor>();
        accessor.Set(new RoutedSettingsSubject("a", routeA));
        var options = scope.ServiceProvider.GetRequiredService<IReadOnlyState<AppSettings>>();
        (await options.GetValueAsync()).Label.ShouldBe("same");
        var changes = Channel.CreateUnbounded<string?>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }
        );
        using var subscription = options.OnChange(value => changes.Writer.TryWrite(value.Label));

        await Task.Delay(100);
        accessor.Set(new RoutedSettingsSubject("b", routeB));
        (await changes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2))).ShouldBe(
            "same"
        );

        users.Set(key, routeA, Fragment("changed-a"));
        await Task.Delay(150);
        changes.Reader.TryRead(out _).ShouldBeFalse();
        users.Set(key, routeB, Fragment("changed-b"));
        (await changes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2))).ShouldBe(
            "changed-b"
        );
    }

    [Test]
    public async Task NamedPerSubjectRegistrationUsesItsConfiguredAccessor()
    {
        var users = new SubjectStateStore<AppSettings.Fragment>();
        var subject = new SettingsSubject("tenant", "named-user");
        users.Set(subject.Key, Fragment("named-value"));
        var services = new ServiceCollection();
        services.AddScoped<MutableSubjectAccessor>();
        services.AddConfiglue(builder =>
            builder.Add<AppSettings>(model =>
            {
                model.StateName = "tenant";
                model.PerSubject<MutableSubjectAccessor>();
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "users",
                            users,
                            resourceKeySelector: current =>
                                current is SettingsSubject typed
                                    ? ResourceKey.From(typed.Key)
                                    : ResourceKey.Default
                        )
                    )
                );
            })
        );

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<MutableSubjectAccessor>().Set(subject);
        var options = scope.ServiceProvider.GetRequiredKeyedService<IReadOnlyState<AppSettings>>(
            "tenant"
        );
        (await options.GetValueAsync()).Label.ShouldBe("named-value");
        var fixedSubjectOptions = provider.GetRequiredKeyedService<ISubjectState<AppSettings>>(
            "tenant"
        );
        (await fixedSubjectOptions.ForSubject(subject).GetValueAsync()).Label.ShouldBe(
            "named-value"
        );
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task InjectedEditSessionsAndInspectionResolveCurrentSubject(
        bool named,
        bool scopedRuntime
    )
    {
        var users = new SubjectStateStore<AppSettings.Fragment>();
        var subject = new SettingsSubject("tenant", "user");
        users.Set(subject.Key, RetryFragment(7));
        users.Set(ResourceKey.Default, RouteKey.Default, RetryFragment(1));
        var stateName = named ? "tenant" : string.Empty;
        var serviceKey = named ? "tenant" : null;

        var services = new ServiceCollection();
        services.AddScoped<MutableSubjectAccessor>();
        services.AddConfiglue(builder =>
            builder.Add<AppSettings>(model =>
            {
                if (named)
                {
                    model.StateName = "tenant";
                }
                model.PerSubject<MutableSubjectAccessor>();
                if (scopedRuntime)
                {
                    model.UseScopedRuntime();
                }
                model.WritePlan = StateWritePlan.DefaultTo(SourceId.From("users"));
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "users",
                            users,
                            writer: users,
                            resourceKeySelector: current =>
                                current is SettingsSubject typed
                                    ? ResourceKey.From(typed.Key)
                                    : ResourceKey.Default
                        )
                    )
                );
            })
        );

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }
        );
        if (named)
        {
            Should.Throw<InvalidOperationException>(() =>
                provider.GetRequiredKeyedService<IConfiglueInspection<AppSettings>>("tenant")
            );
            Should.Throw<InvalidOperationException>(() =>
                provider.GetRequiredKeyedService<IConfiglueEditSessions<AppSettings>>("tenant")
            );
        }
        else
        {
            Should.Throw<InvalidOperationException>(() =>
                provider.GetRequiredService<IConfiglueInspection<AppSettings>>()
            );
            Should.Throw<InvalidOperationException>(() =>
                provider.GetRequiredService<IConfiglueEditSessions<AppSettings>>()
            );
        }

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<MutableSubjectAccessor>().Set(subject);

        IWritableState<AppSettings> state = named
            ? scope.ServiceProvider.GetRequiredKeyedService<IWritableState<AppSettings>>("tenant")
            : scope.ServiceProvider.GetRequiredService<IWritableState<AppSettings>>();
        IConfiglueEditSessions<AppSettings> injectedSessions = named
            ? scope.ServiceProvider.GetRequiredKeyedService<IConfiglueEditSessions<AppSettings>>(
                "tenant"
            )
            : scope.ServiceProvider.GetRequiredService<IConfiglueEditSessions<AppSettings>>();
        IConfiglueInspection<AppSettings> injectedInspection = named
            ? scope.ServiceProvider.GetRequiredKeyedService<IConfiglueInspection<AppSettings>>(
                "tenant"
            )
            : scope.ServiceProvider.GetRequiredService<IConfiglueInspection<AppSettings>>();

        ReferenceEquals(state, injectedSessions).ShouldBeTrue();
        ReferenceEquals(state, injectedInspection).ShouldBeTrue();

        (await state.GetValueAsync()).RetryCount.ShouldBe(7);

        using (var viaState = await ((IConfiglueEditSessions<AppSettings>)state)
            .OpenEditSessionAsync())
        {
            viaState.Value.RetryCount.ShouldBe(7);
        }

        using (var viaInjected = await injectedSessions.OpenEditSessionAsync())
        {
            viaInjected.Value.RetryCount.ShouldBe(7);
            viaInjected.Value.RetryCount = 9;
            await viaInjected.CommitAsync();
        }

        users.Read(subject.Key).Value!.RetryCount.Value.ShouldBe(9);
        (await users.ReadAsync()).Value!.RetryCount.Value.ShouldBe(1);

        var check = injectedInspection.Check();
        var streamed = new List<ConfiglueSourceCheckResult>();
        await foreach (var source in check)
        {
            streamed.Add(source);
        }
        var result = await check.Result;
        result.Status.ShouldBe(ConfiglueCheckStatus.Success);
        var reported = streamed.ShouldHaveSingleItem();
        reported.Source.Resolution.ShouldNotBeNull();
        reported.Source.Resolution!.ResourceKey.ShouldBe(ResourceKey.From(subject.Key));
        reported.Source.Resolution.LogicalSubjectKey.ShouldBe(subject.Key);
        reported.Source.Resolution.Route.ShouldBe(RouteKey.Default);
        _ = stateName;
        _ = serviceKey;
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, true)]
    public async Task InjectedEditSessionKeepsFixedSubjectAfterCurrentSubjectChanges(
        bool named,
        bool scopedRuntime
    )
    {
        var users = new SubjectStateStore<AppSettings.Fragment>();
        var subjectA = new SettingsSubject("tenant", "user-a");
        var subjectB = new SettingsSubject("tenant", "user-b");
        users.Set(subjectA.Key, RetryFragment(7));
        users.Set(subjectB.Key, RetryFragment(8));
        users.Set(ResourceKey.Default, RouteKey.Default, RetryFragment(1));

        var services = new ServiceCollection();
        services.AddScoped<MutableSubjectAccessor>();
        services.AddConfiglue(builder =>
            builder.Add<AppSettings>(model =>
            {
                if (named)
                {
                    model.StateName = "tenant";
                }
                model.PerSubject<MutableSubjectAccessor>();
                if (scopedRuntime)
                {
                    model.UseScopedRuntime();
                }
                model.WritePlan = StateWritePlan.DefaultTo(SourceId.From("users"));
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "users",
                            users,
                            writer: users,
                            resourceKeySelector: current =>
                                current is SettingsSubject typed
                                    ? ResourceKey.From(typed.Key)
                                    : ResourceKey.Default
                        )
                    )
                );
            })
        );

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }
        );
        using var scope = provider.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<MutableSubjectAccessor>();
        accessor.Set(subjectA);

        IConfiglueEditSessions<AppSettings> injectedSessions = named
            ? scope.ServiceProvider.GetRequiredKeyedService<IConfiglueEditSessions<AppSettings>>(
                "tenant"
            )
            : scope.ServiceProvider.GetRequiredService<IConfiglueEditSessions<AppSettings>>();

        using var session = await injectedSessions.OpenEditSessionAsync();
        session.Value.RetryCount.ShouldBe(7);
        accessor.Set(subjectB);
        session.Value.RetryCount = 9;
        await session.CommitAsync();

        users.Read(subjectA.Key).Value!.RetryCount.Value.ShouldBe(9);
        users.Read(subjectB.Key).Value!.RetryCount.Value.ShouldBe(8);
        (await users.ReadAsync()).Value!.RetryCount.Value.ShouldBe(1);
    }

    [Test]
    public async Task InjectedServicesMirrorStateFailureContract()
    {
        var users = new SubjectStateStore<AppSettings.Fragment>();
        var subject = new SettingsSubject("tenant", "user");
        users.Set(subject.Key, RetryFragment(7));
        users.Set(ResourceKey.Default, RouteKey.Default, RetryFragment(1));

        var services = new ServiceCollection();
        services.AddScoped<MutableSubjectAccessor>();
        services.AddConfiglue(builder =>
            builder.Add<AppSettings>(model =>
            {
                model.PerSubject<MutableSubjectAccessor>();
                model.WritePlan = StateWritePlan.DefaultTo(SourceId.From("users"));
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "users",
                            users,
                            writer: users,
                            resourceKeySelector: current =>
                                current is SettingsSubject typed
                                    ? ResourceKey.From(typed.Key)
                                    : ResourceKey.Default
                        )
                    )
                );
            })
        );

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }
        );
        using var scope = provider.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<IWritableState<AppSettings>>();
        var injectedSessions = scope.ServiceProvider.GetRequiredService<
            IConfiglueEditSessions<AppSettings>
        >();
        var injectedInspection = scope.ServiceProvider.GetRequiredService<
            IConfiglueInspection<AppSettings>
        >();
        var stateInspection = (IConfiglueInspection<AppSettings>)state;
        var stateSessions = (IConfiglueEditSessions<AppSettings>)state;

        // Unresolved subject: no subject has been set on this scope's accessor.
        var stateReadError = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await state.GetValueAsync()
        );
        var injectedSessionError = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await injectedSessions.OpenEditSessionAsync()
        );
        injectedSessionError.Message.ShouldBe(stateReadError.Message);
        (await stateInspection.Check().Result).Exception.ShouldBeOfType<InvalidOperationException>();
        (await injectedInspection.Check().Result)
            .Exception.ShouldBeOfType<InvalidOperationException>();

        // Cancellation surfaces identically.
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await state.GetValueAsync(canceled.Token)
        );
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await stateSessions.OpenEditSessionAsync(canceled.Token)
        );
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await injectedSessions.OpenEditSessionAsync(canceled.Token)
        );
    }

#if !NET48
    [Test]
    public async Task HttpContextAccessorMapsRequestIntoTypedSubject()
    {
        var services = new ServiceCollection();
        services.AddHttpContextConfiglueSubjectAccessor<SettingsSubject>(
            context => new SettingsSubject("tenant", context.Request.Headers["X-User"].ToString())
        );
        using var provider = services.BuildServiceProvider();
        var httpContextAccessor = provider.GetRequiredService<IHttpContextAccessor>();
        httpContextAccessor.HttpContext = new DefaultHttpContext();
        httpContextAccessor.HttpContext.Request.Headers["X-User"] = "request-user";
        using var scope = provider.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<
            HttpContextConfiglueSubjectAccessor<SettingsSubject>
        >();

        var subject = await accessor.GetCurrentAsync();
        subject.ShouldBe(new SettingsSubject("tenant", "request-user"));
        (await ((IConfiglueSubjectAccessor)accessor).GetCurrentSubjectAsync()).ShouldBe(subject);
    }

    [Test]
    public async Task SubjectRegistrationBuilderWiresHttpContextAccessor()
    {
        var services = new ServiceCollection();
        services
            .AddConfiglueSubject<SettingsSubject>()
            .FromHttpContext(context => new SettingsSubject(
                "tenant",
                context.Request.Headers["X-User"].ToString()
            ));
        using var provider = services.BuildServiceProvider();
        var httpContextAccessor = provider.GetRequiredService<IHttpContextAccessor>();
        httpContextAccessor.HttpContext = new DefaultHttpContext();
        httpContextAccessor.HttpContext.Request.Headers["X-User"] = "builder-user";
        using var scope = provider.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<
            IConfiglueSubjectAccessor<SettingsSubject>
        >();

        (await accessor.GetCurrentAsync()).ShouldBe(new SettingsSubject("tenant", "builder-user"));
    }

    [Test]
    public async Task BlazorAuthenticationAccessorInvalidatesAndResolvesNewPrincipal()
    {
        var authenticationStateProvider = new TestAuthenticationStateProvider("user-a");
        var services = new ServiceCollection();
        services.AddSingleton<AuthenticationStateProvider>(authenticationStateProvider);
        services.AddBlazorAuthenticationConfiglueSubjectAccessor<SettingsSubject>(
            (principal, _) =>
                ValueTaskCompat.FromResult(
                    new SettingsSubject("tenant", principal.FindFirst("user")!.Value)
                )
        );
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<
            BlazorAuthenticationConfiglueSubjectAccessor<SettingsSubject>
        >();
        var typedAccessor = scope.ServiceProvider.GetRequiredService<
            IConfiglueSubjectAccessor<SettingsSubject>
        >();
        var changeSource =
            scope.ServiceProvider.GetRequiredService<IConfiglueSubjectChangeSource>();
        typedAccessor.ShouldBeSameAs(accessor);
        changeSource.ShouldBeSameAs(accessor);
        var invalidated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = changeSource.OnChange(() => invalidated.TrySetResult());

        (await accessor.GetCurrentAsync()).UserId.ShouldBe("user-a");
        authenticationStateProvider.SetUser("user-b");
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        (await accessor.GetCurrentAsync()).UserId.ShouldBe("user-b");
    }
    [Test]
    public void MultipleBlazorSubjectRegistrationsUseLastChangeSourceAndDisposeSubscriptions()
    {
        var authentication = new TestAuthenticationStateProvider("user-a");
        var services = new ServiceCollection();
        services.AddSingleton<AuthenticationStateProvider>(authentication);
        services.AddBlazorAuthenticationConfiglueSubjectAccessor<SettingsSubject>((_, _) => ValueTask.FromResult(new SettingsSubject("tenant", "user")));
        services.AddBlazorAuthenticationConfiglueSubjectAccessor<RoutedSettingsSubject>((_, _) => ValueTask.FromResult(new RoutedSettingsSubject("other", RouteKey.Default)));
        using var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var first = scope.ServiceProvider.GetRequiredService<IConfiglueSubjectAccessor<SettingsSubject>>();
        var second = scope.ServiceProvider.GetRequiredService<IConfiglueSubjectAccessor<RoutedSettingsSubject>>();
        var sources = scope.ServiceProvider.GetServices<IConfiglueSubjectChangeSource>().ToArray();
        sources.Length.ShouldBe(2);
        sources[0].ShouldBeSameAs(first);
        sources[1].ShouldBeSameAs(second);
        scope.ServiceProvider.GetRequiredService<IConfiglueSubjectChangeSource>().ShouldBeSameAs(second);
        var notifications = 0;
        using var subscription = sources[1].OnChange(() => notifications++);
        authentication.SetUser("user-b");
        notifications.ShouldBe(1);
        scope.Dispose();
        authentication.SetUser("user-c");
        notifications.ShouldBe(1);
    }

    [Test]
    public async Task BlazorSubjectRegistrationsUseTheLastScopedAccessorForInvalidation()
    {
        var authenticationStateProvider = new TestAuthenticationStateProvider("user-a");
        var services = new ServiceCollection();
        services.AddSingleton<AuthenticationStateProvider>(authenticationStateProvider);
        services.AddBlazorAuthenticationConfiglueSubjectAccessor<SettingsSubject>(
            (principal, _) => ValueTaskCompat.FromResult(new SettingsSubject("first", principal.FindFirst("user")!.Value))
        );
        services.AddBlazorAuthenticationConfiglueSubjectAccessor<SettingsSubject>(
            (principal, _) => ValueTaskCompat.FromResult(new SettingsSubject("second", principal.FindFirst("user")!.Value))
        );

        using var scopedProvider = services.BuildServiceProvider();
        using var scoped = scopedProvider.CreateScope();
        var concrete = scoped.ServiceProvider.GetRequiredService<BlazorAuthenticationConfiglueSubjectAccessor<SettingsSubject>>();
        var typedAccessor = scoped.ServiceProvider.GetRequiredService<IConfiglueSubjectAccessor<SettingsSubject>>();
        var source = scoped.ServiceProvider.GetRequiredService<IConfiglueSubjectChangeSource>();
        var notifications = 0;
        using var subscription = source.OnChange(() => notifications++);

        concrete.ShouldBeSameAs(typedAccessor);
        concrete.ShouldBeSameAs(source);
        (await typedAccessor.GetCurrentAsync()).TenantId.ShouldBe("second");
        authenticationStateProvider.SetUser("user-b");
        notifications.ShouldBe(1);
    }

    [Test]
    public async Task BlazorSubjectRegistrationsAcrossSubjectTypesUseTheLastGlobalInvalidationSource()
    {
        var authenticationStateProvider = new TestAuthenticationStateProvider("user-a");
        var services = new ServiceCollection();
        services.AddSingleton<AuthenticationStateProvider>(authenticationStateProvider);
        services.AddBlazorAuthenticationConfiglueSubjectAccessor<SettingsSubject>(
            (principal, _) => ValueTaskCompat.FromResult(new SettingsSubject("first", principal.FindFirst("user")!.Value))
        );
        services.AddBlazorAuthenticationConfiglueSubjectAccessor<RoutedSettingsSubject>(
            (principal, _) => ValueTaskCompat.FromResult(new RoutedSettingsSubject(principal.FindFirst("user")!.Value, RouteKey.From("second")))
        );

        using var scopedProvider = services.BuildServiceProvider();
        using var scoped = scopedProvider.CreateScope();
        var first = scoped.ServiceProvider.GetRequiredService<BlazorAuthenticationConfiglueSubjectAccessor<SettingsSubject>>();
        var second = scoped.ServiceProvider.GetRequiredService<BlazorAuthenticationConfiglueSubjectAccessor<RoutedSettingsSubject>>();
        var source = scoped.ServiceProvider.GetRequiredService<IConfiglueSubjectChangeSource>();
        var notifications = 0;
        using var subscription = source.OnChange(() => notifications++);

        first.ShouldNotBeSameAs(source);
        second.ShouldBeSameAs(source);
        (await scoped.ServiceProvider.GetRequiredService<IConfiglueSubjectAccessor<SettingsSubject>>().GetCurrentAsync())
            .ShouldBe(new SettingsSubject("first", "user-a"));
        (await scoped.ServiceProvider.GetRequiredService<IConfiglueSubjectAccessor<RoutedSettingsSubject>>().GetCurrentAsync())
            .ShouldBe(new RoutedSettingsSubject("user-a", RouteKey.From("second")));

        authenticationStateProvider.SetUser("user-b");
        notifications.ShouldBe(1);
    }

#endif

    private static AppSettings.Fragment Fragment(string? label) =>
        new() { Label = Optional<string?>.Present(label) };

    private static AppSettings.Fragment RetryFragment(int retryCount) =>
        new() { RetryCount = Optional<int>.Present(retryCount) };

    private sealed record SettingsSubject(string TenantId, string UserId) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.FromSegments(TenantId, UserId);
    }

    private sealed record RoutedSettingsSubject(string Name, RouteKey Route) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.FromSegments("same-subject-key");
    }

    private sealed class MutableSubjectAccessor
        : IConfiglueSubjectAccessor<SettingsSubject>,
            IConfiglueSubjectChangeSource
    {
        private readonly object _gate = new();
        private readonly List<Action> _listeners = [];
        private SettingsSubject? _subject;

        public async ValueTask<SettingsSubject> GetCurrentAsync(
            CancellationToken cancellationToken = default
        )
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return _subject
                    ?? throw new InvalidOperationException("A test subject has not been selected.");
            }
        }

        public async ValueTask<IConfiglueSubject> GetCurrentSubjectAsync(
            CancellationToken cancellationToken = default
        ) => await GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        public void Set(SettingsSubject subject)
        {
            Action[] listeners;
            lock (_gate)
            {
                _subject = subject;
                listeners = [.. _listeners];
            }

            foreach (var listener in listeners)
            {
                listener();
            }
        }

        public IDisposable OnChange(Action listener)
        {
            lock (_gate)
            {
                _listeners.Add(listener);
            }

            return new CallbackDisposable(() =>
            {
                lock (_gate)
                {
                    _listeners.Remove(listener);
                }
            });
        }
    }

    private sealed class MutableRoutedSubjectAccessor
        : IConfiglueSubjectAccessor<RoutedSettingsSubject>,
            IConfiglueSubjectChangeSource
    {
        private readonly object _gate = new();
        private readonly List<Action> _listeners = [];
        private RoutedSettingsSubject? _subject;

        public ValueTask<RoutedSettingsSubject> GetCurrentAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return ValueTaskCompat.FromResult(
                    _subject
                        ?? throw new InvalidOperationException(
                            "A test subject has not been selected."
                        )
                );
            }
        }

        public async ValueTask<IConfiglueSubject> GetCurrentSubjectAsync(
            CancellationToken cancellationToken = default
        ) => await GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        public void Set(RoutedSettingsSubject subject)
        {
            Action[] listeners;
            lock (_gate)
            {
                _subject = subject;
                listeners = [.. _listeners];
            }

            foreach (var listener in listeners)
            {
                listener();
            }
        }

        public IDisposable OnChange(Action listener)
        {
            lock (_gate)
            {
                _listeners.Add(listener);
            }

            return new CallbackDisposable(() =>
            {
                lock (_gate)
                {
                    _listeners.Remove(listener);
                }
            });
        }
    }

    private sealed class SubjectStateStore<T> : ISourceReader<T>, ISourceWriter<T>, ISourceWatcher
    {
        private readonly ConcurrentDictionary<
            (ResourceKey Key, RouteKey Route),
            InMemoryStateSource<T>
        > _states = new();

        public void Set(SubjectKey key, T value) =>
            Set(ResourceKey.From(key), RouteKey.Default, value);

        public void Set(SubjectKey key, RouteKey route, T value) =>
            Get(ResourceKey.From(key), route).Set(value);

        public void Set(ResourceKey key, RouteKey route, T value) => Get(key, route).Set(value);

        public StateReadResult<T> Read(SubjectKey key) =>
            Get(ResourceKey.From(key), RouteKey.Default).ReadAsync().GetAwaiter().GetResult();

        public ValueTask<StateReadResult<T>> ReadAsync(
            CancellationToken cancellationToken = default
        ) => Get(ResourceKey.Default, RouteKey.Default).ReadAsync(cancellationToken);

        public ValueTask<StateReadResult<T>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => Get(context.ResourceKey, context.Route).ReadAsync(cancellationToken);

        public ValueTask<StateWriteResult> WriteAsync(
            StateWriteRequest<T> request,
            CancellationToken cancellationToken = default
        ) => Get(ResourceKey.Default, RouteKey.Default).WriteAsync(request, cancellationToken);

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<T> request,
            CancellationToken cancellationToken = default
        ) => Get(context.ResourceKey, context.Route).WriteAsync(request, cancellationToken);

        public ValueTask WaitForChangeAsync(
            string? observedRevision,
            CancellationToken cancellationToken = default
        ) =>
            Get(ResourceKey.Default, RouteKey.Default)
                .WaitForChangeAsync(observedRevision, cancellationToken);

        public ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        ) =>
            Get(context.ResourceKey, context.Route)
                .WaitForChangeAsync(observedRevision, cancellationToken);

        private InMemoryStateSource<T> Get(ResourceKey key, RouteKey route) =>
            _states.GetOrAdd((key, route), static _ => new InMemoryStateSource<T>());
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

#if !NET48
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
#endif
}
