using System.Collections.Concurrent;
using System.Security.Claims;
using System.Threading.Channels;
using Configlue.Sources;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;
#if !NET48
using Configlue.Extensions.AspNetCore;
using Configlue.Extensions.Blazor;
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
                model.WriteRoute = StateWriteRoute.To("users");
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "users",
                            users,
                            writer: users,
                            watcher: users,
                            subjectKeySelector: subject =>
                                subject is SettingsSubject typed ? typed.Key : SubjectKey.Default
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
        provider.GetRequiredService<IConfiglueInspection<AppSettings>>().ShouldNotBeNull();
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
        (resolutionA.ResourceKey).ShouldBe(subjectA.Key);
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
                            subjectKeySelector: subject =>
                                subject is SettingsSubject typed ? typed.Key : SubjectKey.Default
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
                        .KeyBy<RoutedSettingsSubject>(_ => key)
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
                            subjectKeySelector: current =>
                                current is SettingsSubject typed ? typed.Key : SubjectKey.Default
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
        var invalidated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = accessor.OnChange(() => invalidated.TrySetResult());

        (await accessor.GetCurrentAsync()).UserId.ShouldBe("user-a");
        authenticationStateProvider.SetUser("user-b");
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        (await accessor.GetCurrentAsync()).UserId.ShouldBe("user-b");
    }
#endif

    private static AppSettings.Fragment Fragment(string? label) =>
        new() { Label = Optional<string?>.Present(label) };

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

    private sealed class SubjectStateStore<T>
        : ISourceReader<T>,
            ISourceWriter<T>,
            ISourceWatcher
    {
        private readonly ConcurrentDictionary<
            (SubjectKey Key, RouteKey Route),
            InMemoryStateSource<T>
        > _states = new();

        public void Set(SubjectKey key, T value) => Set(key, RouteKey.Default, value);

        public void Set(SubjectKey key, RouteKey route, T value) => Get(key, route).Set(value);

        public StateReadResult<T> Read(SubjectKey key) =>
            Get(key, RouteKey.Default).ReadAsync().GetAwaiter().GetResult();

        public ValueTask<StateReadResult<T>> ReadAsync(
            CancellationToken cancellationToken = default
        ) => Get(SubjectKey.Default, RouteKey.Default).ReadAsync(cancellationToken);

        public ValueTask<StateReadResult<T>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => Get(context.Key, context.Route).ReadAsync(cancellationToken);

        public ValueTask<StateWriteResult> WriteAsync(
            StateWriteRequest<T> request,
            CancellationToken cancellationToken = default
        ) => Get(SubjectKey.Default, RouteKey.Default).WriteAsync(request, cancellationToken);

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<T> request,
            CancellationToken cancellationToken = default
        ) => Get(context.Key, context.Route).WriteAsync(request, cancellationToken);

        public ValueTask WaitForChangeAsync(
            string? observedRevision,
            CancellationToken cancellationToken = default
        ) =>
            Get(SubjectKey.Default, RouteKey.Default)
                .WaitForChangeAsync(observedRevision, cancellationToken);

        public ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        ) =>
            Get(context.Key, context.Route).WaitForChangeAsync(observedRevision, cancellationToken);

        private InMemoryStateSource<T> Get(SubjectKey key, RouteKey route) =>
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
