using Configlue.Extensibility;
using Configlue.Sources;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Tests;

public sealed class ScopedRuntimeCreationFailureTests
{
    [Test]
    public async Task WritePlanValidationFailure_DisposesOwnedResource()
    {
        var resource = new SyncProbe();
        var services = new ServiceCollection();
        services.AddConfiglue(builder =>
            builder.Add<AppSettings>(model =>
            {
                model.UseScopedRuntime();
                model.Sources(sources =>
                    ((IConfiglueSourceRegistrationSink)sources).Add(
                        new OwningDefinition(resource)
                    )
                );
                model.WritePlan = StateWritePlan.DefaultTo(SourceId.From("missing"));
            })
        );
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }
        );
        using var scope = provider.CreateScope();

        var exception = Should.Throw<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IWritableState<AppSettings>>()
        );
        exception.Message.ShouldContain("missing");

        resource.DisposeCallCount.ShouldBe(1);

        await scope.ServiceProvider.DisposeScopeAsyncIfAvailable();
        resource.DisposeCallCount.ShouldBe(1);
    }

    [Test]
    public async Task SourceFactoryFailure_DisposesEarlierOwnedResources()
    {
        var first = new SyncProbe();
        var second = new SyncProbe();
        var services = new ServiceCollection();
        services.AddConfiglue(builder =>
            builder.Add<AppSettings>(model =>
            {
                model.UseScopedRuntime();
                model.Sources(sources =>
                    ((IConfiglueSourceRegistrationSink)sources).Add(
                        new TwoResourceThenFailDefinition(first, second)
                    )
                );
            })
        );
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }
        );
        using var scope = provider.CreateScope();

        var exception = Should.Throw<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IWritableState<AppSettings>>()
        );
        exception.Message.ShouldContain("source factory failed");

        first.DisposeCallCount.ShouldBe(1);
        second.DisposeCallCount.ShouldBe(1);

        await scope.ServiceProvider.DisposeScopeAsyncIfAvailable();
        first.DisposeCallCount.ShouldBe(1);
        second.DisposeCallCount.ShouldBe(1);
    }

    [Test]
    public async Task AsyncOnlyResource_IsDisposedOnScopedCreationFailure()
    {
        var resource = new AsyncOnlyProbe();
        var services = new ServiceCollection();
        services.AddConfiglue(builder =>
            builder.Add<AppSettings>(model =>
            {
                model.UseScopedRuntime();
                model.Sources(sources =>
                    ((IConfiglueSourceRegistrationSink)sources).Add(
                        new OwningDefinition(resource)
                    )
                );
                model.WritePlan = StateWritePlan.DefaultTo(SourceId.From("missing"));
            })
        );
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }
        );
        using var scope = provider.CreateScope();

        Should.Throw<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IWritableState<AppSettings>>()
        );

        resource.DisposeAsyncCallCount.ShouldBe(1);

        await scope.ServiceProvider.DisposeScopeAsyncIfAvailable();
        resource.DisposeAsyncCallCount.ShouldBe(1);
    }

    [Test]
    public async Task BothDisposableResource_PrefersAsyncDisposalOnFailure()
    {
        var resource = new AsyncDisposableProbe();
        var services = new ServiceCollection();
        services.AddConfiglue(builder =>
            builder.Add<AppSettings>(model =>
            {
                model.UseScopedRuntime();
                model.Sources(sources =>
                    ((IConfiglueSourceRegistrationSink)sources).Add(
                        new OwningDefinition(resource)
                    )
                );
                model.WritePlan = StateWritePlan.DefaultTo(SourceId.From("missing"));
            })
        );
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }
        );
        using var scope = provider.CreateScope();

        Should.Throw<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IWritableState<AppSettings>>()
        );

        resource.DisposeAsyncCallCount.ShouldBe(1);
        resource.DisposeCallCount.ShouldBe(0);

        await scope.ServiceProvider.DisposeScopeAsyncIfAvailable();
        resource.DisposeAsyncCallCount.ShouldBe(1);
        resource.DisposeCallCount.ShouldBe(0);
    }

    [Test]
    public async Task CleanupFailure_DisposesRemainingResourcesAndReportsBothFailures()
    {
        var order = new List<string>();
        var first = new OrderProbe("first", order);
        var failing = new ThrowingProbe("failing", order);
        var last = new OrderProbe("last", order);
        var services = new ServiceCollection();
        services.AddConfiglue(builder =>
            builder.Add<AppSettings>(model =>
            {
                model.UseScopedRuntime();
                model.Sources(sources =>
                    ((IConfiglueSourceRegistrationSink)sources).Add(
                        new MultiOwningDefinition([first, failing, last])
                    )
                );
                model.WritePlan = StateWritePlan.DefaultTo(SourceId.From("missing"));
            })
        );
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }
        );
        using var scope = provider.CreateScope();

        Exception exception;
        try
        {
            scope.ServiceProvider.GetRequiredService<IWritableState<AppSettings>>();
            throw new InvalidOperationException("Expected scoped creation to fail.");
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        // Reverse-order cleanup: last and first are still released despite the failure.
        order.ShouldBe(["last", "failing", "first"]);
        first.DisposeCallCount.ShouldBe(1);
        last.DisposeCallCount.ShouldBe(1);

        // The original creation failure must remain visible alongside cleanup errors.
        var flattened = exception.ToString();
        flattened.ShouldContain("missing");
        flattened.ShouldContain("cleanup failed: failing");

        await scope.ServiceProvider.DisposeScopeAsyncIfAvailable();
        first.DisposeCallCount.ShouldBe(1);
        last.DisposeCallCount.ShouldBe(1);
    }

    [Test]
    public async Task BorrowedResource_IsNotDisposedOnScopedCreationFailure()
    {
        var borrowed = new SyncProbe();
        var services = new ServiceCollection();
        services.AddConfiglue(builder =>
            builder.Add<AppSettings>(model =>
            {
                model.UseScopedRuntime();
                model.Sources(sources =>
                    ((IConfiglueSourceRegistrationSink)sources).Add(
                        new BorrowedDefinition(borrowed)
                    )
                );
                model.WritePlan = StateWritePlan.DefaultTo(SourceId.From("missing"));
            })
        );
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }
        );
        using var scope = provider.CreateScope();

        Should.Throw<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IWritableState<AppSettings>>()
        );

        borrowed.DisposeCallCount.ShouldBe(0);

        await scope.ServiceProvider.DisposeScopeAsyncIfAvailable();
        borrowed.DisposeCallCount.ShouldBe(0);
    }

    [Test]
    public async Task DuplicateOwn_IsDisposedOnce()
    {
        var resource = new SyncProbe();
        var services = new ServiceCollection();
        services.AddConfiglue(builder =>
            builder.Add<AppSettings>(model =>
            {
                model.UseScopedRuntime();
                model.Sources(sources =>
                    ((IConfiglueSourceRegistrationSink)sources).Add(
                        new DuplicateOwningDefinition(resource)
                    )
                );
                model.WritePlan = StateWritePlan.DefaultTo(SourceId.From("missing"));
            })
        );
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }
        );
        using var scope = provider.CreateScope();

        Should.Throw<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IWritableState<AppSettings>>()
        );

        resource.DisposeCallCount.ShouldBe(1);

        await scope.ServiceProvider.DisposeScopeAsyncIfAvailable();
        resource.DisposeCallCount.ShouldBe(1);
    }

    [Test]
    public async Task SuccessfulScopedRuntime_DisposesOwnedResourceOnce()
    {
        var resource = new SyncProbe();
        var services = new ServiceCollection();
        services.AddConfiglue(builder =>
            builder.Add<AppSettings>(model =>
            {
                model.UseScopedRuntime();
                model.Sources(sources =>
                    ((IConfiglueSourceRegistrationSink)sources).Add(
                        new OwningDefinition(resource)
                    )
                );
            })
        );
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }
        );
        var scope = provider.CreateScope();
        var state = scope.ServiceProvider.GetRequiredService<IWritableState<AppSettings>>();
        state.ShouldNotBeNull();
        resource.DisposeCallCount.ShouldBe(0);

        await scope.ServiceProvider.DisposeScopeAsyncIfAvailable();
        if (scope is IAsyncDisposable asyncScope)
        {
            await asyncScope.DisposeAsync();
        }
        else
        {
            scope.Dispose();
        }

        resource.DisposeCallCount.ShouldBe(1);
    }

    private sealed class OwningDefinition(object resource) : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            context.Own(resource);
            var store = new InMemoryStateSource<TFragment>();
            return context.Complete(
                new StateSource<TFragment>("owned", store, writer: store)
            );
        }
    }

    private sealed class MultiOwningDefinition(IReadOnlyList<object> resources)
        : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            foreach (var resource in resources)
            {
                context.Own(resource);
            }

            var store = new InMemoryStateSource<TFragment>();
            return context.Complete(
                new StateSource<TFragment>("owned", store, writer: store)
            );
        }
    }

    private sealed class TwoResourceThenFailDefinition(SyncProbe first, SyncProbe second)
        : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            context.Own(first);
            context.Own(second);
            throw new InvalidOperationException("source factory failed");
        }
    }

    private sealed class BorrowedDefinition(SyncProbe borrowed) : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            _ = borrowed;
            var store = new InMemoryStateSource<TFragment>();
            return new ConfiglueSourceCreation<TFragment>(
                new StateSource<TFragment>("borrowed", store, writer: store)
            );
        }
    }

    private sealed class DuplicateOwningDefinition(SyncProbe resource)
        : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            context.Own(resource);
            context.Own(resource);
            var store = new InMemoryStateSource<TFragment>();
            return context.Complete(
                new StateSource<TFragment>("owned", store, writer: store)
            );
        }
    }

    private sealed class SyncProbe : IDisposable
    {
        public int DisposeCallCount { get; private set; }

        public void Dispose() => DisposeCallCount++;
    }

    private sealed class AsyncOnlyProbe : IAsyncDisposable
    {
        public int DisposeAsyncCallCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeAsyncCallCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OrderProbe(string name, List<string> order) : IDisposable
    {
        public int DisposeCallCount { get; private set; }

        public void Dispose()
        {
            DisposeCallCount++;
            order.Add(name);
        }
    }

    private sealed class ThrowingProbe(string name, List<string> order) : IDisposable
    {
        public void Dispose()
        {
            order.Add(name);
            throw new InvalidOperationException($"cleanup failed: {name}");
        }
    }
}

internal static class ScopedRuntimeCreationFailureTestExtensions
{
    public static async Task DisposeScopeAsyncIfAvailable(this IServiceProvider provider)
    {
        if (provider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
    }
}
