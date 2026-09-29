using Configlue.Resource.Dapr;
using Dapr.Client;

namespace Configlue.Tests;

public sealed class DaprStateResourceTests
{
    [Test]
    public async Task ReadAsync_ExposesContentAndEtagAsRevision()
    {
        var client = new FakeDaprStateClient
        {
            Content = new byte[] { 1, 2, 3 },
            ETag = "revision-1",
        };
        var resource = new DaprStateResource(client, "state", "settings");

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Content.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        result.Revision.ShouldBe("revision-1");
    }

    [Test]
    public async Task ReadAsync_TreatsEmptyStateWithoutEtagAsMissing()
    {
        var resource = new DaprStateResource(new FakeDaprStateClient(), "state", "settings");

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.NotFound);
        result.Revision.ShouldBeNull();
    }

    [Test]
    public async Task SubjectAwareAddressSelectsStoreAndKeyWhilePreservingETagChecks()
    {
        var client = new FakeDaprStateClient
        {
            Content = new byte[] { 1, 2, 3 },
            ETag = "revision-1",
            TrySaveResult = true,
        };
        var resource = new DaprStateResource(
            client,
            "default-store",
            "settings",
            new DaprStateResourceOptions
            {
                StoreNameSelector = context => $"store-{context.Route.Value}",
                KeySelector = context => $"settings/{context.Key.Value}",
            }
        );
        var subject = new ResourceSubject("tenant-a");
        var context = new ConfiglueResourceContext(subject, subject.Key, RouteKey.From("jp"));

        var read = await resource.ReadAsync(context);
        await resource.WriteAsync(
            context,
            new ResourceWriteRequest(
                new byte[] { 4 },
                Condition: RevisionCondition.FromRevision(read.Revision)
            )
        );

        read.Revision.ShouldBe("revision-1");
        client.LastStoreName.ShouldBe("store-jp");
        client.LastKey.ShouldBe($"settings/{subject.Key.Value}");
        client.LastETag.ShouldBe("revision-1");
        client.LastConcurrency.ShouldBe(ConcurrencyMode.FirstWrite);
        resource.GetResourceId(context).ShouldNotBe(resource.ResourceId);
    }

    [Test]
    public async Task WriteAsync_UsesFirstWriteForExpectedEtagAndMapsMismatchToConflict()
    {
        var client = new FakeDaprStateClient { TrySaveResult = true };
        var resource = new DaprStateResource(client, "state", "settings");

        var result = await resource.WriteAsync(
            new ResourceWriteRequest(
                new byte[] { 4, 5 },
                Condition: RevisionCondition.FromRevision("revision-1")
            )
        );

        client.LastETag.ShouldBe("revision-1");
        client.LastConcurrency.ShouldBe(ConcurrencyMode.FirstWrite);
        client.LastContent.ToArray().ShouldBe(new byte[] { 4, 5 });
        result.Revision.ShouldBeNull();

        client.TrySaveResult = false;
        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest(
                    new byte[] { 6 },
                    Condition: RevisionCondition.FromRevision("stale")
                )
            )
        );
    }

    [Test]
    public async Task WriteAsync_UsesFirstWriteForExpectedMissingState()
    {
        var client = new FakeDaprStateClient { TrySaveResult = true };
        var resource = new DaprStateResource(client, "state", "settings");

        await resource.WriteAsync(
            new ResourceWriteRequest(new byte[] { 1 }, Condition: RevisionCondition.MustNotExist)
        );

        client.LastETag.ShouldBe(string.Empty);
        client.LastConcurrency.ShouldBe(ConcurrencyMode.FirstWrite);
    }

    [Test]
    public async Task WriteAsync_UsesUnconditionalSaveWhenRevisionIsNotChecked()
    {
        var client = new FakeDaprStateClient();
        var resource = new DaprStateResource(client, "state", "settings");

        var result = await resource.WriteAsync(new ResourceWriteRequest(new byte[] { 9 }));

        client.SaveCount.ShouldBe(1);
        client.TrySaveCount.ShouldBe(0);
        client.LastConcurrency.ShouldBe(ConcurrencyMode.LastWrite);
        result.Revision.ShouldBeNull();
    }

    [Test]
    public void ResourceId_IsStableForStoreAndKeyAndCanBeOverridden()
    {
        var first = new DaprStateResource(new FakeDaprStateClient(), "state", "settings");
        var same = new DaprStateResource(new FakeDaprStateClient(), "state", "settings");
        var different = new DaprStateResource(new FakeDaprStateClient(), "state", "other");
        var overridden = new ResourceId("deployment:settings");
        var withOverride = new DaprStateResource(
            new FakeDaprStateClient(),
            "state",
            "settings",
            new DaprStateResourceOptions { ResourceId = overridden }
        );

        same.ResourceId.ShouldBe(first.ResourceId);
        different.ResourceId.ShouldNotBe(first.ResourceId);
        withOverride.ResourceId.ShouldBe(overridden);
    }

    private sealed class FakeDaprStateClient : IDaprStateClient
    {
        public ReadOnlyMemory<byte> Content { get; init; }
        public string? ETag { get; init; }
        public bool TrySaveResult { get; set; }
        public int SaveCount { get; private set; }
        public int TrySaveCount { get; private set; }
        public string? LastETag { get; private set; }
        public ReadOnlyMemory<byte> LastContent { get; private set; }
        public ConcurrencyMode? LastConcurrency { get; private set; }
        public string? LastStoreName { get; private set; }
        public string? LastKey { get; private set; }

        public Task<(ReadOnlyMemory<byte> Content, string? ETag)> GetByteStateAndETagAsync(
            string storeName,
            string key,
            ConsistencyMode? consistencyMode,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken
        )
        {
            LastStoreName = storeName;
            LastKey = key;
            return Task.FromResult((Content, ETag));
        }

        public Task SaveByteStateAsync(
            string storeName,
            string key,
            ReadOnlyMemory<byte> content,
            StateOptions stateOptions,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken
        )
        {
            LastStoreName = storeName;
            LastKey = key;
            SaveCount++;
            LastContent = content;
            LastConcurrency = stateOptions.Concurrency;
            return Task.CompletedTask;
        }

        public Task<bool> TrySaveByteStateAsync(
            string storeName,
            string key,
            ReadOnlyMemory<byte> content,
            string etag,
            StateOptions stateOptions,
            IReadOnlyDictionary<string, string>? metadata,
            CancellationToken cancellationToken
        )
        {
            LastStoreName = storeName;
            LastKey = key;
            TrySaveCount++;
            LastETag = etag;
            LastContent = content;
            LastConcurrency = stateOptions.Concurrency;
            return Task.FromResult(TrySaveResult);
        }
    }

    private sealed record ResourceSubject(string Name) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From(Name);
    }
}
