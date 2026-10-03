using Configlue.Sources;

namespace Configlue.Tests;

public sealed class StateSourceOptionsTests
{
    [Test]
    public void OptionsAttachCapabilitiesAndPreserveRoutingAndLifetimeMetadata()
    {
        var reader = new CapabilityReader();
        var source = new StateSource<string>(
            "configured",
            reader,
            new StateSourceOptions<string>
            {
                Priority = 17,
                FallbackCondition = StateFallbackCondition.InvalidPayload,
                PhysicalOrigin = "test:memory",
                FixedResourceId = new ResourceId("fixed:one"),
                ExplicitOnly = true,
                RuntimeLifetime = RuntimeLifetimeRequirement.Scoped,
                ModelId = "settings-model",
                ResourceKeySelector = subject => ResourceKey.From("key:" + subject.Key.Value),
                RouteSelector = _ => RouteKey.From("region-a"),
            }
        );

        source.Priority.ShouldBe(17);
        source.FallbackCondition.ShouldBe(StateFallbackCondition.InvalidPayload);
        source.Writer.ShouldBeSameAs(reader.Writer);
        source.Watcher.ShouldBeSameAs(reader.Watcher);
        source.PhysicalOrigin.ShouldBe("test:memory");
        source.FixedResourceId.ShouldBe(new ResourceId("fixed:one"));
        source.ExplicitOnly.ShouldBeTrue();
        source.RuntimeLifetime.ShouldBe(RuntimeLifetimeRequirement.Scoped);
        var context = source.GetResourceContext(new TestSubject("subject"));
        context.ModelId.ShouldBe("settings-model");
        context.ResourceKey.ShouldBe(
            ResourceKey.From("key:" + SubjectKey.From("subject").Value)
        );
        context.Route.ShouldBe(RouteKey.From("region-a"));
    }

    [Test]
    public void ReaderOnlyConstructionKeepsWriteAndWatchAbsent()
    {
        var source = new StateSource<string>(
            "read-only",
            new ReaderOnly(),
            new StateSourceOptions<string> { ExplicitOnly = true }
        );

        source.Writer.ShouldBeNull();
        source.Watcher.ShouldBeNull();
        source.ExplicitOnly.ShouldBeTrue();
    }

    private sealed class ReaderOnly : ISourceReader<string>
    {
        public ValueTask<StateReadResult<string>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(StateReadResult<string>.NotFound());
    }

    private sealed class CapabilityReader : ISourceCapabilities<string>
    {
        public ISourceWriter<string> Writer { get; } = new TestWriter();
        public ISourceWatcher Watcher { get; } = new TestWatcher();

        public ValueTask<StateReadResult<string>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(StateReadResult<string>.NotFound());
    }

    private sealed class TestWriter : ISourceWriter<string>
    {
        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<string> request,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(new StateWriteResult("revision"));
    }

    private sealed class TestWatcher : ISourceWatcher
    {
        public ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        ) => ValueTask.CompletedTask;
    }

    private sealed class TestSubject(string key) : IConfiglueSubject
    {
        public SubjectKey Key { get; } = SubjectKey.From(key);
    }
}
