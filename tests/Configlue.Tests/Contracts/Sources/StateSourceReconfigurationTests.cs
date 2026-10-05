using Configlue.Extensibility;
using Configlue.Sources;

namespace Configlue.Tests;

// Regression coverage for #253: descriptor reconfiguration flows through one internal
// primitive and must preserve every metadata/capability field when only one override
// changes, without freezing operation-context (contextual) resource identity (#101).
public sealed class StateSourceReconfigurationTests
{
    [Test]
    public void SingleOverridePreservesEveryOtherField()
    {
        var baseline = CreateFullyConfiguredSource();

        AssertIdentity(baseline, StateSourceReconfiguration.Reconfigure<string, string>(
            baseline,
            baseline.Reader,
            baseline.Writer,
            id: SourceId.From("renamed")
        ), expectId: SourceId.From("renamed"));

        var priority = StateSourceReconfiguration.Reconfigure<string, string>(
            baseline,
            baseline.Reader,
            baseline.Writer,
            priority: 99
        );
        priority.Priority.ShouldBe(99);
        AssertIdentity(baseline, priority, expectId: baseline.Id, skipPriority: true);

        var fallback = StateSourceReconfiguration.Reconfigure<string, string>(
            baseline,
            baseline.Reader,
            baseline.Writer,
            fallbackCondition: StateFallbackCondition.Unavailable
        );
        fallback.FallbackCondition.ShouldBe(StateFallbackCondition.Unavailable);
        AssertIdentity(baseline, fallback, expectId: baseline.Id, skipFallback: true);

        var readOnly = StateSourceReconfiguration.Reconfigure<string, string>(
            baseline,
            baseline.Reader,
            null
        );
        readOnly.Writer.ShouldBeNull();
        readOnly.Watcher.ShouldBeSameAs(baseline.Watcher);
        AssertIdentity(baseline, readOnly, expectId: baseline.Id, skipWriter: true);

        var explicitOnly = StateSourceReconfiguration.Reconfigure<string, string>(
            baseline,
            baseline.Reader,
            baseline.Writer,
            explicitOnly: false
        );
        explicitOnly.ExplicitOnly.ShouldBeFalse();
        AssertIdentity(baseline, explicitOnly, expectId: baseline.Id, skipExplicitOnly: true);

        var lifetime = StateSourceReconfiguration.Reconfigure<string, string>(
            baseline,
            baseline.Reader,
            baseline.Writer,
            runtimeLifetime: RuntimeLifetimeRequirement.Shared
        );
        lifetime.RuntimeLifetime.ShouldBe(RuntimeLifetimeRequirement.Shared);
        AssertIdentity(baseline, lifetime, expectId: baseline.Id, skipLifetime: true);

        var model = StateSourceReconfiguration.Reconfigure<string, string>(
            baseline,
            baseline.Reader,
            baseline.Writer,
            modelId: "model-2",
            replaceModelId: true
        );
        model.ModelId.ShouldBe("model-2");
        AssertIdentity(baseline, model, expectId: baseline.Id, skipModelId: true);

        var clearedModel = StateSourceReconfiguration.Reconfigure<string, string>(
            baseline,
            baseline.Reader,
            baseline.Writer,
            modelId: null,
            replaceModelId: true
        );
        clearedModel.ModelId.ShouldBeNull();
        AssertIdentity(baseline, clearedModel, expectId: baseline.Id, skipModelId: true);

        Func<IConfiglueSubject, ResourceKey> nextKeys = subject =>
            ResourceKey.From("next:" + subject.Key.Value);
        var keys = StateSourceReconfiguration.Reconfigure<string, string>(
            baseline,
            baseline.Reader,
            baseline.Writer,
            resourceKeySelector: nextKeys
        );
        var keySubject = new TestSubject("a");
        keys.GetResourceKey(keySubject).ShouldBe(ResourceKey.From("next:" + keySubject.Key.Value));
        keys.GetResourceId(keySubject).ShouldBe(new ResourceId("ctx:next:" + keySubject.Key.Value));
        AssertIdentity(baseline, keys, expectId: baseline.Id, skipKeySelector: true, skipResourceId: true);

        var owned = StateSourceReconfiguration.Reconfigure<string, string>(
            baseline,
            baseline.Reader,
            baseline.Writer,
            ownedPropertyPaths: ["Replaced"]
        );
        owned.OwnedPropertyPaths.ShouldBe(["Replaced"]);
        AssertIdentity(baseline, owned, expectId: baseline.Id, skipOwnedPaths: true);
    }

    [Test]
    public void CrossTypeReconfigurePreservesRoutingMetadata()
    {
        var baseline = CreateFullyConfiguredSource();
        var reader = new StubReader<int>();
        var writer = new StubWriter<int>();
        var projected = StateSourceReconfiguration.Reconfigure<string, int>(
            baseline,
            reader,
            writer
        );

        projected.Id.ShouldBe(baseline.Id);
        projected.Reader.ShouldBeSameAs(reader);
        projected.Writer.ShouldBeSameAs(writer);
        projected.Priority.ShouldBe(baseline.Priority);
        projected.FallbackCondition.ShouldBe(baseline.FallbackCondition);
        projected.Watcher.ShouldBeSameAs(baseline.Watcher);
        projected.PhysicalOrigin.ShouldBe(baseline.PhysicalOrigin);
        projected.ConfiguredResourceId.ShouldBe(baseline.ConfiguredResourceId);
        projected.ExplicitOnly.ShouldBe(baseline.ExplicitOnly);
        projected.RuntimeLifetime.ShouldBe(baseline.RuntimeLifetime);
        projected.ModelId.ShouldBe(baseline.ModelId);
        projected.OwnedPropertyPaths.ShouldBe(baseline.OwnedPropertyPaths);
        var subject = new TestSubject("a");
        projected.GetResourceKey(subject).ShouldBe(baseline.GetResourceKey(subject));
        projected.GetRouteKey(subject).ShouldBe(baseline.GetRouteKey(subject));
    }

    [Test]
    public void WithHelpersPreserveOwnedPathsAndRouting()
    {
        var baseline = CreateFullyConfiguredSource();
        var keyed = baseline.WithResourceKeySelector(static subject =>
            ResourceKey.From("other:" + subject.Key.Value));
        // WithResourceKeySelector must not drop write-ownership metadata.
        var keySubject = new TestSubject("a");
        keyed.OwnedPropertyPaths.ShouldBe(baseline.OwnedPropertyPaths);
        keyed.GetResourceKey(keySubject).ShouldBe(ResourceKey.From("other:" + keySubject.Key.Value));
        keyed.ModelId.ShouldBe(baseline.ModelId);

        var stamped = baseline.WithModelId("model-2");
        stamped.ModelId.ShouldBe("model-2");
        stamped.OwnedPropertyPaths.ShouldBe(baseline.OwnedPropertyPaths);

        var sameModel = baseline.WithModelId(baseline.ModelId);
        sameModel.ShouldBeSameAs(baseline);

        var mounted = baseline.WithWriteOwnership("Nested");
        mounted.OwnedPropertyPaths.ShouldBe(["Nested.Owned"]);
        mounted.Priority.ShouldBe(baseline.Priority);
        mounted.Writer.ShouldBeSameAs(baseline.Writer);
    }

    [Test]
    public void ContextualIdentitySurvivesReconfigurationAndProjection()
    {
        var resource = new ContextualReader();
        var baseline = new StateSource<string>(
            "contextual",
            resource,
            new StateSourceOptions<string> { Watcher = new TestWatcher() }
        );
        var first = new TestSubject("a");
        var second = new TestSubject("b");
        var firstId = baseline.GetResourceId(first);
        var secondId = baseline.GetResourceId(second);
        firstId.ShouldNotBe(secondId);

        var reconfigured = StateSourceReconfiguration.Reconfigure<string, string>(
            baseline,
            baseline.Reader,
            baseline.Writer,
            priority: 5
        );
        reconfigured.GetResourceId(first).ShouldBe(firstId);
        reconfigured.GetResourceId(second).ShouldBe(secondId);

        var projected = StateSourceProjection.Project<string, int>(
            baseline,
            static _ => 1,
            static _ => "reverse"
        );
        projected.GetResourceId(first).ShouldBe(firstId);
        projected.GetResourceId(second).ShouldBe(secondId);

        var fixedSource = new StateSource<string>(
            "fixed",
            resource,
            new StateSourceOptions<string> { FixedResourceId = new ResourceId("fixed:override") }
        );
        var fixedReconfigured = StateSourceReconfiguration.Reconfigure<string, string>(
            fixedSource,
            fixedSource.Reader,
            fixedSource.Writer,
            priority: 5
        );
        fixedReconfigured.GetResourceId(first).ShouldBe(new ResourceId("fixed:override"));
        fixedReconfigured.GetResourceId(second).ShouldBe(new ResourceId("fixed:override"));
    }

    [Test]
    public void RegistrationApplyPreservesContextualIdentityAndMetadata()
    {
        var reader = new ContextualFragmentReader();
        var baseline = new StateSource<AppSettings.Fragment>(
            "original",
            reader,
            new StateSourceOptions<AppSettings.Fragment>
            {
                Priority = 3,
                PhysicalOrigin = "test:origin",
                ResourceKeySelector = subject => ResourceKey.From("rk:" + subject.Key.Value),
                RouteSelector = _ => RouteKey.From("route-a"),
                ModelId = "model-1",
            }
        ).WithWriteOwnership("Owned");
        var first = new TestSubject("a");
        var second = new TestSubject("b");
        var firstId = baseline.GetResourceId(first);
        firstId.ShouldNotBe(baseline.GetResourceId(second));

        var registration = new ConfiglueSourceRegistration(() => { });
        registration.Named("renamed").Priority(10).ExplicitOnly().ScopedRuntime();
        var applied = registration.Apply(baseline);

        applied.Id.ShouldBe(SourceId.From("renamed"));
        applied.Priority.ShouldBe(10);
        applied.ExplicitOnly.ShouldBeTrue();
        applied.RuntimeLifetime.ShouldBe(RuntimeLifetimeRequirement.Scoped);
        applied.Reader.ShouldBeSameAs(baseline.Reader);
        applied.Watcher.ShouldBeSameAs(baseline.Watcher);
        applied.PhysicalOrigin.ShouldBe("test:origin");
        applied.ConfiguredResourceId.ShouldBeNull();
        applied.ModelId.ShouldBe("model-1");
        applied.OwnedPropertyPaths.ShouldBe(["Owned"]);
        applied.GetResourceKey(first).ShouldBe(ResourceKey.From("rk:" + first.Key.Value));
        applied.GetRouteKey(first).ShouldBe(RouteKey.From("route-a"));
        applied.GetResourceId(first).ShouldBe(firstId);
        applied.GetResourceId(second).ShouldBe(baseline.GetResourceId(second));

        var untouched = new ConfiglueSourceRegistration(() => { });
        untouched.Apply(baseline).ShouldBeSameAs(baseline);

        var readOnly = new ConfiglueSourceRegistration(() => { });
        readOnly.ReadOnly();
        readOnly.Apply(baseline).Writer.ShouldBeNull();

        var writable = new ConfiglueSourceRegistration(() => { });
        writable.Writable();
        Should.Throw<InvalidOperationException>(() => writable.Apply(baseline));
    }

    private static StateSource<string> CreateFullyConfiguredSource()
    {
        var reader = new ContextualReader();
        var source = new StateSource<string>(
            "original",
            reader,
            new StateSourceOptions<string>
            {
                Priority = 7,
                FallbackCondition = StateFallbackCondition.NotFoundOrUnavailable,
                Writer = new TestWriter(),
                Watcher = new TestWatcher(),
                PhysicalOrigin = "test:origin",
                ExplicitOnly = true,
                ResourceKeySelector = subject => ResourceKey.From("rk:" + subject.Key.Value),
                RuntimeLifetime = RuntimeLifetimeRequirement.Scoped,
                ModelId = "model-1",
                RouteSelector = _ => RouteKey.From("route-a"),
            }
        );
        return source.WithWriteOwnership("Owned");
    }

    private static void AssertIdentity(
        StateSource<string> expected,
        StateSource<string> actual,
        SourceId expectId,
        bool skipKeySelector = false,
        bool skipWriter = false,
        bool skipExplicitOnly = false,
        bool skipModelId = false,
        bool skipOwnedPaths = false,
        bool skipPriority = false,
        bool skipFallback = false,
        bool skipLifetime = false,
        bool skipResourceId = false
    )
    {
        actual.Id.ShouldBe(expectId);
        actual.Reader.ShouldBeSameAs(expected.Reader);
        if (!skipPriority)
        {
            actual.Priority.ShouldBe(expected.Priority);
        }
        if (!skipFallback)
        {
            actual.FallbackCondition.ShouldBe(expected.FallbackCondition);
        }
        if (skipWriter || expected.Writer is null)
        {
            actual.Writer.ShouldBeNull();
        }
        else
        {
            actual.Writer.ShouldBeSameAs(expected.Writer);
        }
        actual.Watcher.ShouldBeSameAs(expected.Watcher);
        actual.PhysicalOrigin.ShouldBe(expected.PhysicalOrigin);
        actual.ConfiguredResourceId.ShouldBe(expected.ConfiguredResourceId);
        if (!skipExplicitOnly)
        {
            actual.ExplicitOnly.ShouldBe(expected.ExplicitOnly);
        }
        if (!skipLifetime)
        {
            actual.RuntimeLifetime.ShouldBe(expected.RuntimeLifetime);
        }
        if (!skipModelId)
        {
            actual.ModelId.ShouldBe(expected.ModelId);
        }
        if (!skipOwnedPaths)
        {
            actual.OwnedPropertyPaths.ShouldBe(expected.OwnedPropertyPaths.ToArray());
        }
        var subject = new TestSubject("a");
        if (!skipKeySelector)
        {
            actual.GetResourceKey(subject).ShouldBe(expected.GetResourceKey(subject));
        }
        actual.GetRouteKey(subject).ShouldBe(expected.GetRouteKey(subject));
        if (!skipResourceId)
        {
            actual.GetResourceId(subject).ShouldBe(expected.GetResourceId(subject));
        }
    }

    private sealed class TestSubject(string key) : IConfiglueSubject
    {
        public SubjectKey Key { get; } = SubjectKey.From(key);
    }

    private sealed class ContextualReader : ISourceReader<string>, ITryResourceIdentity
    {
        public bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId resourceId)
        {
            resourceId = new ResourceId($"ctx:{context.ResourceKey.Value}");
            return true;
        }

        public ValueTask<StateReadResult<string>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => ValueTaskCompat.FromResult(StateReadResult<string>.NotFound());
    }

    private sealed class ContextualFragmentReader
        : ISourceReader<AppSettings.Fragment>,
            ITryResourceIdentity
    {
        public bool TryGetResourceId(ConfiglueResourceContext context, out ResourceId resourceId)
        {
            resourceId = new ResourceId($"ctx:{context.ResourceKey.Value}");
            return true;
        }

        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => ValueTaskCompat.FromResult(StateReadResult<AppSettings.Fragment>.NotFound());
    }

    private sealed class TestWriter : ISourceWriter<string>
    {
        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<string> request,
            CancellationToken cancellationToken = default
        ) => ValueTaskCompat.FromResult(new StateWriteResult("revision"));
    }

    private sealed class TestWatcher : ISourceWatcher
    {
        public ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        ) => ValueTask.CompletedTask;
    }

    private sealed class StubReader<T> : ISourceReader<T>
    {
        public ValueTask<StateReadResult<T>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => ValueTaskCompat.FromResult(StateReadResult<T>.NotFound());
    }

    private sealed class StubWriter<T> : ISourceWriter<T>
    {
        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<T> request,
            CancellationToken cancellationToken = default
        ) => ValueTaskCompat.FromResult(new StateWriteResult("revision"));
    }
}
