using Configlue.Sources;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Tests;

public sealed class StateCheckTests
{
    [Test]
    public void DiagnosticsSurfaceExposesCheckAndHidesInspectionAcquisition()
    {
        typeof(IConfiglueDiagnostics<AppSettings>).GetMethod("Check").ShouldNotBeNull();
        typeof(IConfiglueDiagnostics<AppSettings>)
            .GetMethod("GetDiagnostics")
            .ShouldNotBeNull();
        typeof(ConfiglueContext).GetMethod("GetInspection").ShouldBeNull();
        typeof(ConfiglueApp).GetMethod("GetInspection").ShouldBeNull();
        typeof(Configlue.IConfiglueDiagnostics<AppSettings>).Assembly
            .GetType("Configlue.IConfiglueInspection`1")
            .ShouldBeNull();
    }

    [Test]
    public async Task CheckStreamsEachEvaluatedSourceInPriorityOrder()
    {
        var primary = new InMemoryStateSource<AppSettings.Fragment>(Fragment("primary"));
        var secondary = new InMemoryStateSource<AppSettings.Fragment>(Fragment("secondary"));
        await using var runtime = CreateRuntime(
            Source("primary", primary, priority: 10),
            Source("secondary", secondary, priority: 0)
        );
        var check = runtime.Check();

        var streamed = new List<ConfiglueSourceCheckResult>();
        await foreach (var source in check)
        {
            streamed.Add(source);
        }

        var result = await check.Result;

        streamed.Count.ShouldBe(2);
        streamed[0].Source.Kind.ShouldBe("Custom");
        streamed[0].Status.ShouldBe(ConfiglueCheckStatus.Success);
        streamed[0].Contributed.ShouldBeTrue();
        streamed[0].FallbackContinued.ShouldBeFalse();
        streamed[1].Status.ShouldBe(ConfiglueCheckStatus.Success);
        streamed[1].Contributed.ShouldBeTrue();
        result.Status.ShouldBe(ConfiglueCheckStatus.Success);
        result.IsResolved.ShouldBeTrue();
    }

    [Test]
    public async Task CheckContinuesFallbackAndReportsEachAttemptedSource()
    {
        var primary = new InMemoryStateSource<AppSettings.Fragment>();
        primary.SetUnavailable();
        var secondary = new InMemoryStateSource<AppSettings.Fragment>(Fragment("secondary"));
        await using var runtime = CreateRuntime(
            Source(
                "primary",
                primary,
                priority: 10,
                fallbackCondition: StateFallbackCondition.NotFoundOrUnavailable
            ),
            Source("secondary", secondary, priority: 0)
        );
        var check = runtime.Check();

        var streamed = new List<ConfiglueSourceCheckResult>();
        await foreach (var source in check)
        {
            streamed.Add(source);
        }

        var result = await check.Result;

        streamed.Count.ShouldBe(2);
        streamed[0].Status.ShouldBe(ConfiglueCheckStatus.Unavailable);
        streamed[0].Contributed.ShouldBeFalse();
        streamed[0].FallbackContinued.ShouldBeTrue();
        streamed[1].Status.ShouldBe(ConfiglueCheckStatus.Success);
        streamed[1].Contributed.ShouldBeTrue();
        result.Status.ShouldBe(ConfiglueCheckStatus.Success);
    }

    [Test]
    public async Task CheckStopsAtNonFallbackSourceWithoutInventingResults()
    {
        var primary = new InMemoryStateSource<AppSettings.Fragment>();
        primary.SetUnavailable();
        var secondary = new InMemoryStateSource<AppSettings.Fragment>(Fragment("secondary"));
        await using var runtime = CreateRuntime(
            Source(
                "primary",
                primary,
                priority: 10,
                fallbackCondition: StateFallbackCondition.None
            ),
            Source("secondary", secondary, priority: 0)
        );
        var check = runtime.Check();

        var streamed = new List<ConfiglueSourceCheckResult>();
        await foreach (var source in check)
        {
            streamed.Add(source);
        }

        var result = await check.Result;

        streamed.Count.ShouldBe(1);
        streamed[0].Status.ShouldBe(ConfiglueCheckStatus.Unavailable);
        streamed[0].FallbackContinued.ShouldBeFalse();
        result.Status.ShouldBe(ConfiglueCheckStatus.Unavailable);
    }

    [Test]
    public async Task CheckReportsNotFoundWhenNonFallbackSourceHasNoValue()
    {
        var primary = new InMemoryStateSource<AppSettings.Fragment>();
        primary.SetNotFound();
        await using var runtime = CreateRuntime(
            Source("primary", primary, priority: 10, fallbackCondition: StateFallbackCondition.None)
        );
        var check = runtime.Check();

        var streamed = new List<ConfiglueSourceCheckResult>();
        await foreach (var source in check)
        {
            streamed.Add(source);
        }

        var result = await check.Result;

        streamed.ShouldHaveSingleItem().Status.ShouldBe(ConfiglueCheckStatus.NotFound);
        result.Status.ShouldBe(ConfiglueCheckStatus.NotFound);
    }

    [Test]
    public async Task CheckReportsFaultedSourceAndFaultedResult()
    {
        await using var runtime = CreateRuntime(
            Source("throwing", new ThrowingReader<AppSettings.Fragment>())
        );
        var check = runtime.Check();

        var streamed = new List<ConfiglueSourceCheckResult>();
        await foreach (var source in check)
        {
            streamed.Add(source);
        }

        var result = await check.Result;

        var faulted = streamed.ShouldHaveSingleItem();
        faulted.Status.ShouldBe(ConfiglueCheckStatus.Faulted);
        faulted.Exception.ShouldBeOfType<InvalidOperationException>();
        result.Status.ShouldBe(ConfiglueCheckStatus.Faulted);
        result.Exception.ShouldBeOfType<InvalidOperationException>();
    }

    [Test]
    public async Task CheckReadsEachSourceExactlyOnceRegardlessOfResultFirst()
    {
        var primary = new CountingReader<AppSettings.Fragment>(
            StateReadResult<AppSettings.Fragment>.Success(Fragment("primary"))
        );
        var secondary = new CountingReader<AppSettings.Fragment>(
            StateReadResult<AppSettings.Fragment>.Success(Fragment("secondary"))
        );
        await using var runtime = CreateRuntime(
            Source("primary", primary, priority: 10),
            Source("secondary", secondary, priority: 0)
        );
        var check = runtime.Check();

        var result = await check.Result;
        var streamed = new List<ConfiglueSourceCheckResult>();
        await foreach (var source in check)
        {
            streamed.Add(source);
        }

        result.Status.ShouldBe(ConfiglueCheckStatus.Success);
        streamed.Count.ShouldBe(2);
        primary.Reads.ShouldBe(1);
        secondary.Reads.ShouldBe(1);
    }

    [Test]
    public async Task CheckNeverEnumeratedStillCompletesItsResult()
    {
        var reader = new CountingReader<AppSettings.Fragment>(
            StateReadResult<AppSettings.Fragment>.Success(Fragment("value"))
        );
        await using var runtime = CreateRuntime(Source("primary", reader));

        var check = runtime.Check();
        var result = await check.Result;

        result.Status.ShouldBe(ConfiglueCheckStatus.Success);
        reader.Reads.ShouldBe(1);
    }

    [Test]
    public async Task CheckAbandonedEnumerationStillCompletesItsResult()
    {
        var second = new CountingReader<AppSettings.Fragment>(
            StateReadResult<AppSettings.Fragment>.Success(Fragment("second"))
        );
        await using var runtime = CreateRuntime(
            Source(
                "primary",
                new CountingReader<AppSettings.Fragment>(
                    StateReadResult<AppSettings.Fragment>.Success(Fragment("primary"))
                ),
                priority: 10
            ),
            Source("secondary", second, priority: 0)
        );
        var check = runtime.Check();

        await foreach (var _ in check)
        {
            break;
        }

        var result = await check.Result;

        result.Status.ShouldBe(ConfiglueCheckStatus.Success);
        second.Reads.ShouldBe(1);
    }

    [Test]
    public async Task CheckSupportsConcurrentEnumerationAndResult()
    {
        await using var runtime = CreateRuntime(
            Source(
                "primary",
                new CountingReader<AppSettings.Fragment>(
                    StateReadResult<AppSettings.Fragment>.Success(Fragment("primary"))
                )
            )
        );
        var check = runtime.Check();

        var resultTask = check.Result.AsTask();
        var streamed = new List<ConfiglueSourceCheckResult>();
        await foreach (var source in check)
        {
            streamed.Add(source);
        }

        var result = await resultTask;

        streamed.ShouldHaveSingleItem().Contributed.ShouldBeTrue();
        result.Status.ShouldBe(ConfiglueCheckStatus.Success);
    }

    [Test]
    public async Task CheckEnumeratesOnlyOnce()
    {
        await using var runtime = CreateRuntime(
            Source(
                "primary",
                new CountingReader<AppSettings.Fragment>(
                    StateReadResult<AppSettings.Fragment>.Success(Fragment("primary"))
                )
            )
        );
        var check = runtime.Check();
        await using (var first = check.GetAsyncEnumerator())
        {
            (await first.MoveNextAsync()).ShouldBeTrue();
        }

        Should
            .Throw<InvalidOperationException>(() => check.GetAsyncEnumerator())
            .Message.ShouldContain("enumerated only once");
        (await check.Result).Status.ShouldBe(ConfiglueCheckStatus.Success);
    }

    [Test]
    public async Task CheckSurfacesCancellationAsCancellation()
    {
        await using var runtime = CreateRuntime(
            Source(
                "primary",
                new CountingReader<AppSettings.Fragment>(
                    StateReadResult<AppSettings.Fragment>.Success(Fragment("primary"))
                )
            )
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var check = runtime.Check(cancellation.Token);
        await Should.ThrowAsync<OperationCanceledException>(async () => await check.Result);
    }

    [Test]
    public async Task CheckCanBeBoundToAnExplicitSubject()
    {
        var reader = new SubjectReader();
        var subject = new CheckSubject("tenant-a");
        var builder = new StateSourceSetBuilder<AppSettings.Fragment>();
        builder.Add("users", reader).ResourceKeyBy<CheckSubject>(static current => ResourceKey.From(current.Key));
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            builder.Build(),
            onChangeDebounce: TimeSpan.Zero
        );
        var diagnostics = (IConfiglueDiagnostics<AppSettings>)runtime.ForSubject(subject);

        var check = diagnostics.Check();
        var streamed = new List<ConfiglueSourceCheckResult>();
        await foreach (var source in check)
        {
            streamed.Add(source);
        }

        var result = await check.Result;

        result.Status.ShouldBe(ConfiglueCheckStatus.Success);
        var sourceResult = streamed.ShouldHaveSingleItem();
        sourceResult.Source.Resolution.ShouldNotBeNull();
        sourceResult.Source.Resolution!.ResourceKey.ShouldBe(ResourceKey.From(subject.Key));
    }

    [Test]
    public async Task CheckThroughCurrentSubjectFacadeBindsTheScopeSubject()
    {
        var reader = new SubjectReader();
        var subject = new CheckSubject("tenant-scoped");
        var services = new ServiceCollection();
        services.AddScoped<CheckAccessor>();
        services.AddConfiglue(builder =>
            builder.Add<AppSettings>(model =>
            {
                model.PerSubject<CheckAccessor>();
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "users",
                            reader,
                            resourceKeySelector: current =>
                                current is CheckSubject typed
                                    ? ResourceKey.From(typed.Key)
                                    : ResourceKey.Default
                        )
                    )
                );
            })
        );
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<CheckAccessor>().Set(subject);
        var state = scope.ServiceProvider.GetRequiredService<IReadOnlyState<AppSettings>>();
        var diagnostics = (IConfiglueDiagnostics<AppSettings>)state;

        var check = diagnostics.Check();
        var streamed = new List<ConfiglueSourceCheckResult>();
        await foreach (var source in check)
        {
            streamed.Add(source);
        }

        var result = await check.Result;

        result.Status.ShouldBe(ConfiglueCheckStatus.Success);
        streamed.ShouldHaveSingleItem().Source.Resolution!.ResourceKey.ShouldBe(ResourceKey.From(subject.Key));
    }

    private static ConfiglueRuntime<AppSettings, AppSettings.Fragment> CreateRuntime(
        params StateSource<AppSettings.Fragment>[] sources
    ) => new(new StateSourceSet<AppSettings.Fragment>(sources), onChangeDebounce: TimeSpan.Zero);

    private static StateSource<AppSettings.Fragment> Source(
        string id,
        ISourceReader<AppSettings.Fragment> reader,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound
    ) => new(id, reader, priority, fallbackCondition);

    private static AppSettings.Fragment Fragment(string? label) =>
        new() { Label = Optional<string?>.Present(label) };

    private sealed record CheckSubject(string TenantId) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.FromSegments(TenantId);
    }

    private sealed class CheckAccessor : IConfiglueSubjectAccessor<CheckSubject>
    {
        private CheckSubject? _subject;

        public void Set(CheckSubject subject) => _subject = subject;

        public ValueTask<CheckSubject> GetCurrentAsync(
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTaskCompat.FromResult(
                _subject
                    ?? throw new InvalidOperationException("A test subject has not been selected.")
            );
        }

        public async ValueTask<IConfiglueSubject> GetCurrentSubjectAsync(
            CancellationToken cancellationToken = default
        ) => await GetCurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class CountingReader<T>(StateReadResult<T> result) : ISourceReader<T>
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public ValueTask<StateReadResult<T>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _reads);
            return ValueTaskCompat.FromResult(result);
        }
    }

    private sealed class ThrowingReader<T> : ISourceReader<T>
    {
        public ValueTask<StateReadResult<T>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("reader failed");
        }
    }

    private sealed class SubjectReader : ISourceReader<AppSettings.Fragment>
    {
        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTaskCompat.FromResult(
                StateReadResult<AppSettings.Fragment>.Success(Fragment(context.ResourceKey.Value))
            );
        }
    }
}
