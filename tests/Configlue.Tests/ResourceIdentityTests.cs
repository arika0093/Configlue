using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class ResourceIdentityTests
{
    [Test]
    public async Task SectionViewsAndSerializedSourcesRetainThePhysicalResourceIdentity()
    {
        var resource = new InMemoryResource();
        var json = new JsonSectionResource(resource, "App:Json");
        var xml = new XmlSectionResource(resource, "App:Xml");
        var yaml = new YamlSectionResource(resource, "App:Yaml");
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "json",
            json,
            new Configlue.Provider.Json.JsonStateCodec<AppSettings.Fragment>()
        );
        var projected = StateSourceProjection.Project(
            source,
            static fragment => fragment,
            static fragment => fragment
        );

        (json.ResourceId).ShouldBe(resource.ResourceId);
        (xml.ResourceId).ShouldBe(resource.ResourceId);
        (yaml.ResourceId).ShouldBe(resource.ResourceId);
        (source.ResourceId).ShouldBe(resource.ResourceId);
        (projected.ResourceId).ShouldBe(resource.ResourceId);
    }

    [Test]
    public async Task FileResourcesForTheSameNormalizedPathHaveTheSameIdentity()
    {
        var path = Path.Combine(Path.GetTempPath(), $"configlue-{Guid.NewGuid():N}.json");
        var first = new FileResource(path);
        var second = new FileResource(
            Path.Combine(Path.GetDirectoryName(path)!, ".", Path.GetFileName(path))
        );

        (first.ResourceId).ShouldBe(second.ResourceId);
        (first.ResourceId.Value).ShouldStartWith("file:");
    }
}
