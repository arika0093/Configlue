using Configlue.Sources;

namespace Configlue.Tests;

public sealed class StateSourceWriterTests
{
    [Test]
    public async Task ExplicitDefault_RoutesRepeatedWritesToSelectedSource()
    {
        var first = new RecordingStore();
        var second = new RecordingStore();
        var firstSource = new StateSource<string>("first", first, new StateSourceOptions<string> { Writer = first });
        var secondSource = new StateSource<string>("second", second, new StateSourceOptions<string> { Writer = second });
        var writer = new StateSourceWriter<string>(
            new StateSourceSet<string>([firstSource, secondSource]),
            SourceId.From("second")
        );

        writer.WriteSource.ShouldBeSameAs(secondSource);
        for (var index = 0; index < 3; index++)
        {
            await writer.WriteAsync(new StateWriteRequest<string>($"value-{index}"));
        }

        first.WriteCount.ShouldBe(0);
        second.WriteCount.ShouldBe(3);
        second.LastRequest!.Value.ShouldBe("value-2");
    }

    [Test]
    public async Task InferredSingleWritableRoot_RoutesWrites()
    {
        var policy = new ReaderOnlyStore();
        var user = new RecordingStore();
        var userSource = new StateSource<string>("user", user, new StateSourceOptions<string> { Priority = 0, Writer = user });
        var writer = new StateSourceWriter<string>(
            new StateSourceSet<string>(
            [
                new StateSource<string>("policy", policy, new StateSourceOptions<string> { Priority = 100 }),
                userSource,
            ])
        );

        writer.WriteSource.ShouldBeSameAs(userSource);
        await writer.WriteAsync(new StateWriteRequest<string>("edited"));

        user.WriteCount.ShouldBe(1);
    }

    [Test]
    public async Task ExplicitOnlySource_IsExcludedFromInference()
    {
        var ordinary = new RecordingStore();
        var explicitOnly = new RecordingStore();
        var ordinarySource = new StateSource<string>("ordinary", ordinary, new StateSourceOptions<string> { Writer = ordinary });
        var writer = new StateSourceWriter<string>(
            new StateSourceSet<string>(
            [
                ordinarySource,
                new StateSource<string>("explicit", explicitOnly, new StateSourceOptions<string> { Writer = explicitOnly, ExplicitOnly = true }),
            ])
        );

        writer.WriteSource.ShouldBeSameAs(ordinarySource);
        await writer.WriteAsync(new StateWriteRequest<string>("edited"));

        ordinary.WriteCount.ShouldBe(1);
        explicitOnly.WriteCount.ShouldBe(0);
    }

    [Test]
    public void OwnedSubtreeSource_IsExcludedFromInference()
    {
        var store = new RecordingStore();
        var mounted = new StateSource<string>("mounted", store, new StateSourceOptions<string> { Writer = store }).WithWriteOwnership(
            "Database"
        );

        mounted.OwnedPropertyPaths.Count.ShouldBe(1);
        var exception = Should.Throw<InvalidOperationException>(() =>
            new StateSourceWriter<string>(new StateSourceSet<string>([mounted]))
        );

        exception.Message.ShouldContain("No writable root");
    }

    [Test]
    public async Task NonDefaultContext_ForwardsSourceSpecificContext()
    {
        var store = new RecordingStore();
        var source = new StateSource<string>("writer", store, new StateSourceOptions<string> { Writer = store });
        var writer = new StateSourceWriter<string>(new StateSourceSet<string>([source]));
        var subject = new WriterTestSubject("subject");

        await writer.WriteAsync(
            source.GetResourceContext(subject),
            new StateWriteRequest<string>("edited")
        );

        store.WriteCount.ShouldBe(1);
        store.LastContext.ShouldBe(source.GetResourceContext(subject));
    }

    [Test]
    public void UnknownDefault_IsRejectedAtConstruction()
    {
        var store = new RecordingStore();
        var exception = Should.Throw<InvalidOperationException>(() =>
            new StateSourceWriter<string>(
                new StateSourceSet<string>([new StateSource<string>("only", store, new StateSourceOptions<string> { Writer = store })]),
                SourceId.From("missing")
            )
        );

        exception.Message.ShouldContain("missing");
        exception.Message.ShouldContain("is not registered");
    }

    [Test]
    public void DefaultWithoutWriteCapability_IsRejectedAtConstruction()
    {
        var readOnly = new ReaderOnlyStore();
        var writable = new RecordingStore();
        var exception = Should.Throw<InvalidOperationException>(() =>
            new StateSourceWriter<string>(
                new StateSourceSet<string>(
                [
                    new StateSource<string>("readonly", readOnly, new StateSourceOptions<string>()),
                    new StateSource<string>("writable", writable, new StateSourceOptions<string> { Writer = writable }),
                ]),
                SourceId.From("readonly")
            )
        );

        exception.Message.ShouldContain("readonly");
        exception.Message.ShouldContain("does not support writes");
    }

    [Test]
    public void EmptyDefaultSourceId_IsRejectedAtConstruction()
    {
        var store = new RecordingStore();
        Should.Throw<ArgumentException>(() =>
            new StateSourceWriter<string>(
                new StateSourceSet<string>([new StateSource<string>("only", store, new StateSourceOptions<string> { Writer = store })]),
                default(SourceId)
            )
        );
    }

    [Test]
    public void MissingWritableRoot_IsRejectedAtConstruction()
    {
        var readOnly = new ReaderOnlyStore();
        var exception = Should.Throw<InvalidOperationException>(() =>
            new StateSourceWriter<string>(
                new StateSourceSet<string>([new StateSource<string>("readonly", readOnly, new StateSourceOptions<string>())])
            )
        );

        exception.Message.ShouldContain("No writable root");
    }

    [Test]
    public void AmbiguousWritableRoots_AreRejectedAtConstruction()
    {
        var first = new RecordingStore();
        var second = new RecordingStore();
        var exception = Should.Throw<InvalidOperationException>(() =>
            new StateSourceWriter<string>(
                new StateSourceSet<string>(
                [
                    new StateSource<string>("first", first, new StateSourceOptions<string> { Writer = first }),
                    new StateSource<string>("second", second, new StateSourceOptions<string> { Writer = second }),
                ])
            )
        );

        exception.Message.ShouldContain("Multiple writable root");
    }

    private sealed class RecordingStore : ISourceReader<string>, ISourceWriter<string>
    {
        public int WriteCount { get; private set; }

        public ConfiglueResourceContext LastContext { get; private set; }

        public StateWriteRequest<string>? LastRequest { get; private set; }

        public ValueTask<StateReadResult<string>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            _ = cancellationToken;
            return ValueTaskCompat.FromResult(StateReadResult<string>.NotFound());
        }

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<string> request,
            CancellationToken cancellationToken = default
        )
        {
            _ = cancellationToken;
            LastContext = context;
            LastRequest = request;
            WriteCount++;
            return ValueTaskCompat.FromResult(new StateWriteResult("revision"));
        }
    }

    private sealed class ReaderOnlyStore : ISourceReader<string>
    {
        public ValueTask<StateReadResult<string>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            _ = cancellationToken;
            return ValueTaskCompat.FromResult(StateReadResult<string>.NotFound());
        }
    }

    private sealed class WriterTestSubject(string key) : IConfiglueSubject
    {
        public SubjectKey Key { get; } = SubjectKey.From(key);
    }
}
