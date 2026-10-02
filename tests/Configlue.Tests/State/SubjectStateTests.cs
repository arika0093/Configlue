using System.Collections.Concurrent;
using Configlue.Sources;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Tests;

public sealed class SubjectStateTests
{
    [Test]
    public async Task FixedSubjectStatesShareSourcesForReadsAndConcurrentWrites()
    {
        var global = new SubjectStateStore<AppSettings.Fragment>();
        global.Set(SubjectKey.Default, Fragment("global"));
        var tenants = new SubjectStateStore<AppSettings.Fragment>();
        tenants.Set(SubjectKey.FromSegments("tenant-a"), Fragment("tenant-a"));
        tenants.Set(SubjectKey.FromSegments("tenant-b"), Fragment("tenant-b"));
        var users = new SubjectStateStore<AppSettings.Fragment>();
        users.Set(SubjectKey.FromSegments("tenant-a", "user-1"), Fragment("user-1"));
        users.Set(SubjectKey.FromSegments("tenant-b", "user-2"), Fragment("user-2"));

        var builder = new StateSourceSetBuilder<AppSettings.Fragment>();
        builder
            .Add("user", users, priority: 300)
            .ResourceKeyBy<SettingsSubject>(static s =>
                ResourceKey.From(SubjectKey.FromSegments(s.TenantId, s.UserId))
            );
        builder
            .Add("tenant", tenants, priority: 200)
            .WithoutWriter()
            .ResourceKeyBy<SettingsSubject>(static s =>
                ResourceKey.From(SubjectKey.FromSegments(s.TenantId))
            );
        builder
            .Add("server", global, priority: 100)
            .WithoutWriter()
            .ResourceKeyBy<SettingsSubject>(static _ => ResourceKey.Default);

        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            builder.Build(),
            onChangeDebounce: TimeSpan.Zero
        );
        ISubjectState<AppSettings> subjectOptions = runtime;
        var a = subjectOptions.ForSubject(new SettingsSubject("tenant-a", "user-1"));
        var b = subjectOptions.ForSubject(new SettingsSubject("tenant-b", "user-2"));

        (await a.GetValueAsync()).Label.ShouldBe("user-1");
        (await b.GetValueAsync()).Label.ShouldBe("user-2");
        (
            await subjectOptions
                .ForSubject(new SettingsSubject("tenant-a", "missing"))
                .GetValueAsync()
        ).Label.ShouldBe("tenant-a");

        await Task.WhenAll(
            a.SaveAsync(new AppSettings.Patch { Label = FragmentOperation<string?>.Set("saved-a") })
                .AsTask(),
            b.SaveAsync(new AppSettings.Patch { Label = FragmentOperation<string?>.Set("saved-b") })
                .AsTask()
        );

        (await a.GetValueAsync()).Label.ShouldBe("saved-a");
        (await b.GetValueAsync()).Label.ShouldBe("saved-b");
        users
            .Read(SubjectKey.FromSegments("tenant-a", "user-1"))
            .Value!.Label.Value.ShouldBe("saved-a");
        users
            .Read(SubjectKey.FromSegments("tenant-b", "user-2"))
            .Value!.Label.Value.ShouldBe("saved-b");
        global.Read(SubjectKey.Default).Value!.Label.Value.ShouldBe("global");
    }

    [Test]
    public void SubjectKeySegmentsAreCanonicalAndUnambiguous()
    {
        SubjectKey
            .FromSegments("tenant/a", "user%1")
            .ShouldNotBe(SubjectKey.FromSegments("tenant", "a/user%1"));
        SubjectKey.FromSegments("e\u0301").ShouldBe(SubjectKey.FromSegments("\u00e9"));
        SubjectKey.Default.IsDefault.ShouldBeTrue();
        SubjectKey.From("default").IsDefault.ShouldBeFalse();
    }

    [Test]
    public async Task DependencyInjectionExposesSubjectViewsFromTheSharedRuntime()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(Fragment("server"));
        var services = new ServiceCollection();
        services.AddConfiglueState<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([new("server", store)])
        );
        using var provider = services.BuildServiceProvider();
        var subjectOptions = provider.GetRequiredService<ISubjectState<AppSettings>>();

        (
            await subjectOptions.ForSubject(new SettingsSubject("tenant", "user")).GetValueAsync()
        ).Label.ShouldBe("server");
    }

    [Test]
    public async Task FixedSubjectChangeNotificationsWatchOnlyThatSubjectKey()
    {
        var users = new SubjectStateStore<AppSettings.Fragment>();
        var a = new SettingsSubject("tenant", "user-a");
        var b = new SettingsSubject("tenant", "user-b");
        users.Set(a.Key, Fragment("before"));
        users.Set(b.Key, Fragment("other"));
        var builder = new StateSourceSetBuilder<AppSettings.Fragment>();
        builder.Add("user", users).ResourceKeyBy<SettingsSubject>(static s => ResourceKey.From(s.Key));
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            builder.Build(),
            onChangeDebounce: TimeSpan.Zero
        );
        var changed = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = runtime
            .ForSubject(a)
            .OnChange(value => changed.TrySetResult(value.Label));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        users.Set(b.Key, Fragment("other changed"));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        changed.Task.IsCompleted.ShouldBeFalse();
        users.Set(a.Key, Fragment("after"));
        (await changed.Task.WaitAsync(TimeSpan.FromSeconds(2))).ShouldBe("after");
    }

    private static AppSettings.Fragment Fragment(string? label) =>
        new() { Label = Optional<string?>.Present(label) };

    private sealed record SettingsSubject(string TenantId, string UserId) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.FromSegments(TenantId, UserId);
    }

    private sealed class SubjectStateStore<T>
        : ISourceReader<T>,
            ISourceWriter<T>,
            ISourceWatcher
    {
        private readonly ConcurrentDictionary<ResourceKey, InMemoryStateSource<T>> _states = new();

        public void Set(SubjectKey key, T value) => Get(ResourceKey.From(key)).Set(value);

        public StateReadResult<T> Read(SubjectKey key) =>
            Get(ResourceKey.From(key)).ReadAsync().GetAwaiter().GetResult();

        public ValueTask<StateReadResult<T>> ReadAsync(
            CancellationToken cancellationToken = default
        ) => Get(ResourceKey.Default).ReadAsync(cancellationToken);

        public ValueTask<StateReadResult<T>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => Get(context.ResourceKey).ReadAsync(cancellationToken);

        public ValueTask<StateWriteResult> WriteAsync(
            StateWriteRequest<T> request,
            CancellationToken cancellationToken = default
        ) => Get(ResourceKey.Default).WriteAsync(request, cancellationToken);

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<T> request,
            CancellationToken cancellationToken = default
        ) => Get(context.ResourceKey).WriteAsync(request, cancellationToken);

        public ValueTask WaitForChangeAsync(
            string? observedRevision,
            CancellationToken cancellationToken = default
        ) => Get(ResourceKey.Default).WaitForChangeAsync(observedRevision, cancellationToken);

        public ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        ) => Get(context.ResourceKey).WaitForChangeAsync(observedRevision, cancellationToken);

        private InMemoryStateSource<T> Get(ResourceKey key) =>
            _states.GetOrAdd(key, static _ => new InMemoryStateSource<T>());
    }
}
