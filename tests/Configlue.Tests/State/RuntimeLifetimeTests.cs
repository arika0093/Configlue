using Configlue.Resource.WebStorage;
using Configlue.Sources;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Configlue.Tests;

[ConfiglueModel("runtime-lifetime-settings", Version = 1)]
public partial class RuntimeLifetimeSettings
{
    public string? Label { get; set; }
}

public sealed partial class RuntimeLifetimeTests
{
    [Test]
    public void SharedSourceKeepsConfigurationRuntimeSharedAcrossScopes()
    {
        var store = new InMemoryStateStore<RuntimeLifetimeSettings.Fragment>();
        var services = new ServiceCollection();
        services.AddConfiglue(builder =>
            builder.Add<RuntimeLifetimeSettings>(model =>
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<RuntimeLifetimeSettings.Fragment>(
                            "store",
                            store,
                            writer: store
                        )
                    )
                )
            )
        );
        using var provider = services.BuildServiceProvider();
        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();

        var stateA = scopeA.ServiceProvider.GetRequiredService<
            IReadOnlyState<RuntimeLifetimeSettings>
        >();
        var stateB = scopeB.ServiceProvider.GetRequiredService<
            IReadOnlyState<RuntimeLifetimeSettings>
        >();

        ReferenceEquals(stateA, stateB).ShouldBeTrue();
        provider
            .GetRequiredService<ConfiglueContext>()
            .GetState<RuntimeLifetimeSettings>()
            .ShouldNotBeNull();
    }

    [Test]
    public void ScopedWebStorageSourceCreatesRuntimePerScope()
    {
        var services = new ServiceCollection();
        services.AddSingleton<FakeJsRuntime>();
        services.AddScoped<IJSRuntime>(provider => provider.GetRequiredService<FakeJsRuntime>());
        services.AddConfiglue(builder =>
            builder.Add<RuntimeLifetimeSettings>(model => model.UseLocalStorage("lifetime-ui"))
        );
        using var provider = services.BuildServiceProvider();
        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();

        var stateA = scopeA.ServiceProvider.GetRequiredService<
            IReadOnlyState<RuntimeLifetimeSettings>
        >();
        var stateB = scopeB.ServiceProvider.GetRequiredService<
            IReadOnlyState<RuntimeLifetimeSettings>
        >();

        ReferenceEquals(stateA, stateB).ShouldBeFalse();
        Should.Throw<KeyNotFoundException>(() =>
            provider.GetRequiredService<ConfiglueContext>().GetState<RuntimeLifetimeSettings>()
        );
    }

    [Test]
    public async Task ScopedWebStorageRuntimePersistsThroughItsScopedJavaScriptRuntime()
    {
        var services = new ServiceCollection();
        services.AddSingleton<FakeJsRuntime>();
        services.AddScoped<IJSRuntime>(provider => provider.GetRequiredService<FakeJsRuntime>());
        services.AddConfiglue(builder =>
            builder.Add<RuntimeLifetimeSettings>(model => model.UseLocalStorage("lifetime-ui"))
        );
        using var provider = services.BuildServiceProvider();
        var jsRuntime = provider.GetRequiredService<FakeJsRuntime>();
        using var scope = provider.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<
            IWritableState<RuntimeLifetimeSettings>
        >();

        await state.SaveAsync(
            new RuntimeLifetimeSettings.Patch
            {
                Label = FragmentOperation<string?>.Set("from-browser"),
            }
        );
        (await state.GetValueAsync()).Label.ShouldBe("from-browser");
        jsRuntime.Values.ShouldContainKey("lifetime-ui");
    }

    [Test]
    public void ProcessWideContextRejectsScopedOnlySource()
    {
        var builder = new ConfiglueBuilder();
        builder.Add<RuntimeLifetimeSettings>(model => model.UseLocalStorage("lifetime-ui"));

        var exception = Should.Throw<InvalidOperationException>(() => builder.CreateContext());
        exception.Message.ShouldContain("scoped");
    }

    [Test]
    public async Task WebStorageResourceReportsUnavailableAndConflicts()
    {
        var fake = new FakeJsRuntime();
        var resource = new WebStorageResource(fake, WebStorageKind.Local, "direct");

        (await resource.ReadAsync()).Status.ShouldBe(StateReadStatus.NotFound);

        var first = await resource.WriteAsync(new ResourceWriteRequest("one"u8.ToArray()));
        first.Revision.ShouldNotBeNullOrEmpty();
        var read = await resource.ReadAsync();
        read.Status.ShouldBe(StateReadStatus.Success);
        read.Revision.ShouldBe(first.Revision);

        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest("two"u8.ToArray(), RevisionCondition.Match("stale"))
            )
        );

        fake.Available = false;
        (await resource.ReadAsync()).Status.ShouldBe(StateReadStatus.Unavailable);
        await Should.ThrowAsync<WebStorageUnavailableException>(async () =>
            await resource.WriteAsync(new ResourceWriteRequest("three"u8.ToArray()))
        );
    }

    private sealed class FakeJsRuntime : IJSRuntime
    {
        public bool Available { get; set; } = true;

        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, default, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args
        )
        {
            EnsureAvailable();
            var key = (string)args![0]!;
            if (identifier.EndsWith(".getItem", StringComparison.Ordinal))
            {
                Values.TryGetValue(key, out var value);
                return ValueTask.FromResult((TValue)(object?)value!);
            }

            if (identifier.EndsWith(".setItem", StringComparison.Ordinal))
            {
                Values[key] = (string)args[1]!;
                return ValueTask.FromResult(default(TValue)!);
            }

            throw new NotSupportedException(identifier);
        }

        public ValueTask InvokeVoidAsync(string identifier, object?[]? args) =>
            InvokeVoidAsync(identifier, default, args);

        public ValueTask InvokeVoidAsync(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args
        )
        {
            EnsureAvailable();
            if (!identifier.EndsWith(".setItem", StringComparison.Ordinal))
            {
                throw new NotSupportedException(identifier);
            }

            var key = (string)args![0]!;
            Values[key] = (string)args[1]!;
            return ValueTask.CompletedTask;
        }

        private void EnsureAvailable()
        {
            if (!Available)
            {
                throw new InvalidOperationException("JavaScript is unavailable.");
            }
        }
    }
}
