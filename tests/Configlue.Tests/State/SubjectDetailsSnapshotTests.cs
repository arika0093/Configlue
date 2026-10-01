using System.Collections.Concurrent;
using Configlue.Sources;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Tests;

public sealed class SubjectDetailsSnapshotTests
{
    [Test]
    public async Task ExplicitlyBoundSubjectDetailsReflectThatSubject()
    {
        var users = new SubjectStateStore<AppSettings.Fragment>();
        var subjectA = new SettingsSubject("tenant-a", "user-a");
        var subjectB = new SettingsSubject("tenant-b", "user-b");
        users.Set(subjectA.Key, Fragment("user-a"));
        users.Set(subjectB.Key, Fragment("user-b"));
        var builder = new StateSourceSetBuilder<AppSettings.Fragment>();
        builder.Add("user", users).KeyBy<SettingsSubject>(static subject => subject.Key);
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            builder.Build(),
            onChangeDebounce: TimeSpan.Zero
        );

        var detailsA = await runtime.ForSubject(subjectA).GetDetailsAsync();
        var detailsB = await runtime.ForSubject(subjectB).GetDetailsAsync();

        (detailsA.Label.Value).ShouldBe("user-a");
        (detailsA.Label.Source).ShouldNotBeNull();
        (detailsB.Label.Value).ShouldBe("user-b");
        (detailsB.Label.Source).ShouldNotBeNull();
    }

    [Test]
    public async Task RoutedSubjectsProduceDistinctDetailsSnapshots()
    {
        var store = new RoutedStateStore();
        var defaultSubject = new RoutingSubject("default", DataStrict: false);
        var tokyoSubject = new RoutingSubject("strict-jp", DataStrict: true);
        store.Set(RouteKey.Default, defaultSubject.Key, Fragment("default"));
        store.Set(RouteKey.From("strict-jp"), tokyoSubject.Key, Fragment("tokyo"));
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                {
                    var routed = new StateSourceSetBuilder<AppSettings.Fragment>();
                    routed
                        .Add("routed", store)
                        .RouteBy<RoutingSubject>(subject =>
                            subject.DataStrict
                                ? RouteKey.From(subject.Region)
                                : RouteKey.Default
                        );
                    sources.Add(routed.Build().Sources[0]);
                });
            });
        });
        var subjectState = context.GetSubjectState<AppSettings>();

        var defaultDetails = await subjectState.ForSubject(defaultSubject).GetDetailsAsync();
        var tokyoDetails = await subjectState.ForSubject(tokyoSubject).GetDetailsAsync();

        (defaultDetails.Label.Value).ShouldBe("default");
        (tokyoDetails.Label.Value).ShouldBe("tokyo");
        store
            .ReadContexts.Any(context =>
                ReferenceEquals(context.Subject, defaultSubject)
                && context.Route == RouteKey.Default
            )
            .ShouldBeTrue();
        store
            .ReadContexts.Any(context =>
                ReferenceEquals(context.Subject, tokyoSubject)
                && context.Route == RouteKey.From("strict-jp")
            )
            .ShouldBeTrue();
    }

    [Test]
    public async Task CurrentSubjectFacadeCanReadGeneratedDetails()
    {
        var users = new SubjectStateStore<AppSettings.Fragment>();
        var subject = new SettingsSubject("tenant", "user");
        users.Set(subject.Key, Fragment("scoped"));
        using var provider = CreateProvider<MutableSubjectAccessor>(users);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<MutableSubjectAccessor>().Set(subject);
        var options = scope.ServiceProvider.GetRequiredService<IReadOnlyState<AppSettings>>();

        var details = await options.GetDetailsAsync();

        (details.Label.Value).ShouldBe("scoped");
        (details.Label.Source).ShouldNotBeNull();
    }

    [Test]
    public async Task CurrentSubjectIsResolvedOncePerDetailsOperationAndTracksChanges()
    {
        var users = new SubjectStateStore<AppSettings.Fragment>();
        var subjectA = new SettingsSubject("tenant", "user-a");
        var subjectB = new SettingsSubject("tenant", "user-b");
        users.Set(subjectA.Key, Fragment("user-a"));
        users.Set(subjectB.Key, Fragment("user-b"));
        using var provider = CreateProvider<CountingSubjectAccessor>(users);
        using var scope = provider.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<CountingSubjectAccessor>();
        var options = scope.ServiceProvider.GetRequiredService<IReadOnlyState<AppSettings>>();
        accessor.Set(subjectA);

        var first = await options.GetDetailsAsync();
        (first.Label.Value).ShouldBe("user-a");
        (accessor.ResolveCount).ShouldBe(1);

        accessor.Set(subjectB);
        var second = await options.GetDetailsAsync();
        (second.Label.Value).ShouldBe("user-b");
        (accessor.ResolveCount).ShouldBe(2);
    }

    [Test]
    public async Task SubjectDetailsPreserveCancellationAndAccessorFailures()
    {
        var users = new SubjectStateStore<AppSettings.Fragment>();
        var subject = new SettingsSubject("tenant", "user");
        users.Set(subject.Key, Fragment("user"));
        var builder = new StateSourceSetBuilder<AppSettings.Fragment>();
        builder.Add("user", users).KeyBy<SettingsSubject>(static current => current.Key);
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            builder.Build(),
            onChangeDebounce: TimeSpan.Zero
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await runtime.ForSubject(subject).GetDetailsAsync(cancellation.Token)
        );

        using var provider = CreateProvider<MutableSubjectAccessor>(users);
        using var scope = provider.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IReadOnlyState<AppSettings>>();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await options.GetDetailsAsync()
        );
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await options.GetDetailsAsync(cancellation.Token)
        );
    }

    private static ServiceProvider CreateProvider<TAccessor>(
        SubjectStateStore<AppSettings.Fragment> users
    )
        where TAccessor : class, IConfiglueSubjectAccessor
    {
        var services = new ServiceCollection();
        services.AddScoped<TAccessor>();
        services.AddConfiglue(builder =>
            builder.Add<AppSettings>(model =>
            {
                model.PerSubject<TAccessor>();
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
        return services.BuildServiceProvider();
    }

    private static AppSettings.Fragment Fragment(string? label) =>
        new() { Label = Optional<string?>.Present(label) };

    private sealed record SettingsSubject(string TenantId, string UserId) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.FromSegments(TenantId, UserId);
    }

    private sealed record RoutingSubject(string Region, bool DataStrict) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From("same-logical-subject");
    }

    private abstract class SubjectAccessorBase
        : IConfiglueSubjectAccessor<SettingsSubject>,
            IConfiglueSubjectChangeSource
    {
        private readonly object _gate = new();
        private readonly List<Action> _listeners = [];
        private SettingsSubject? _subject;
        private int _resolveCount;

        public int ResolveCount => Volatile.Read(ref _resolveCount);

        public ValueTask<SettingsSubject> GetCurrentAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _resolveCount);
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

    private sealed class MutableSubjectAccessor : SubjectAccessorBase { }

    private sealed class CountingSubjectAccessor : SubjectAccessorBase { }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
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

    private sealed class RoutedStateStore
        : ISourceReader<AppSettings.Fragment>,
            ISourceWriter<AppSettings.Fragment>,
            ISourceWatcher
    {
        private readonly ConcurrentDictionary<
            (SubjectKey Key, RouteKey Route),
            StateReadResult<AppSettings.Fragment>
        > _states = new();

        public ConcurrentQueue<ConfiglueResourceContext> ReadContexts { get; } = new();

        public void Set(RouteKey route, SubjectKey key, AppSettings.Fragment value) =>
            _states[(key, route)] = StateReadResult<AppSettings.Fragment>.Success(
                value,
                $"revision:{route.Value}"
            );

        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            CancellationToken cancellationToken = default
        ) => ReadAsync(ConfiglueResourceContext.Default, cancellationToken);

        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadContexts.Enqueue(context);
            return ValueTaskCompat.FromResult(
                _states.GetValueOrDefault((context.Key, context.Route))
            );
        }

        public ValueTask<StateWriteResult> WriteAsync(
            StateWriteRequest<AppSettings.Fragment> request,
            CancellationToken cancellationToken = default
        ) => WriteAsync(ConfiglueResourceContext.Default, request, cancellationToken);

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<AppSettings.Fragment> request,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revision = $"revision:{context.Route.Value}:{Guid.NewGuid():N}";
            _states[(context.Key, context.Route)] = StateReadResult<AppSettings.Fragment>.Success(
                request.Value,
                revision
            );
            return ValueTaskCompat.FromResult(new StateWriteResult(revision));
        }

        public ValueTask WaitForChangeAsync(
            string? observedRevision,
            CancellationToken cancellationToken = default
        ) => ValueTask.CompletedTask;

        public ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        ) => ValueTask.CompletedTask;
    }
}
