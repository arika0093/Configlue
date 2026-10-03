using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Resource.Zip;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class ResourceIdentityTests
{
    [Test]
    public void SectionViewsAndSerializedSourcesRetainThePhysicalResourceIdentity()
    {
        var resource = new ContextSensitiveReader();
        var subject = new ResourceIdentitySubject("tenant-a");
        var context = new ConfiglueResourceContext(subject, ResourceKey.From(subject.Key), RouteKey.Default);
        var json = new JsonSectionResource(resource, "App:Json");
        var xml = new XmlSectionResource(resource, "App:Xml");
        var yaml = new YamlSectionResource(resource, "App:Yaml");
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "json",
            json,
            new global::Configlue.Provider.Json.JsonStateCodec<AppSettings.Fragment>()
        );
        var projected = StateSourceProjection.Project(
            source,
            static fragment => fragment,
            static fragment => fragment
        );
        var transforming = new TransformingResource(resource, [new PassthroughTransformer()]);

        var expected = resource.GetResourceId(context);
        json.GetResourceId(context).ShouldBe(expected);
        xml.GetResourceId(context).ShouldBe(expected);
        yaml.GetResourceId(context).ShouldBe(expected);
        source.GetResourceId(context).ShouldBe(expected);
        projected.GetResourceId(context).ShouldBe(expected);
        transforming.TryGetResourceId(context, out var transformedId).ShouldBeTrue();
        transformedId.ShouldBe(expected);
    }

    [Test]
    public void FileResourcesForTheSameNormalizedPathHaveTheSameIdentity()
    {
        var path = Path.Combine(Path.GetTempPath(), $"configlue-{Guid.NewGuid():N}.json");
        var first = new FileResource(path);
        var second = new FileResource(
            Path.Combine(Path.GetDirectoryName(path)!, ".", Path.GetFileName(path))
        );

        (first.ResourceId).ShouldBe(second.ResourceId);
        (first.ResourceId.Value).ShouldStartWith("file:");
    }

    [Test]
    public void WrappersKeepUnknownPhysicalIdentityUnknown()
    {
        var resource = new UnknownIdentityReader();
        var context = ConfiglueResourceContext.Default;
        var section = new JsonSectionResource(resource, "App:Settings");
        var transforming = new TransformingResource(resource, [new PassthroughTransformer()]);
        var zipEntry = new ZipEntryResource(resource, "settings.bin");
        var serializedReader = new SerializedStateReader<AppSettings.Fragment>(
            resource,
            new global::Configlue.Provider.Json.JsonStateCodec<AppSettings.Fragment>()
        );
        var serialized = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "json",
            resource,
            new global::Configlue.Provider.Json.JsonStateCodec<AppSettings.Fragment>()
        );

        section.TryGetResourceId(context, out _).ShouldBeFalse();
        transforming.TryGetResourceId(context, out _).ShouldBeFalse();
        zipEntry.TryGetResourceId(context, out _).ShouldBeFalse();
        serializedReader.TryGetResourceId(context, out _).ShouldBeFalse();
        serialized.TryGetResourceId(context, out _).ShouldBeFalse();
    }

    private sealed record ResourceIdentitySubject(string Tenant) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.FromSegments(Tenant);
    }

    private sealed class ContextSensitiveReader : IResourceReader, IResourceIdentity
    {
        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => ValueTaskCompat.FromResult(ResourceReadResult.NotFound());

        public ResourceId GetResourceId(ConfiglueResourceContext context)
        {
            if (ReferenceEquals(context.Subject, ConfiglueResourceContext.DefaultSubject))
            {
                throw new InvalidOperationException("Identity requires an operation subject.");
            }

            return new ResourceId($"tenant:{context.ResourceKey.Value}");
        }
    }

    private sealed class UnknownIdentityReader : IResourceReader
    {
        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => ValueTaskCompat.FromResult(ResourceReadResult.NotFound());
    }

    private sealed class PassthroughTransformer : ISynchronousStateByteTransformer
    {
        public ReadOnlyMemory<byte> TransformRead(ReadOnlyMemory<byte> source) => source;

        public ReadOnlyMemory<byte> TransformWrite(ReadOnlyMemory<byte> source) => source;
    }
}
