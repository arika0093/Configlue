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
            new Configlue.Provider.Json.JsonStateCodec<AppSettings.Fragment>());
        var projected = StateSourceProjection.Project(
            source,
            static fragment => fragment,
            static fragment => fragment);

        await Assert.That(json.ResourceId).IsEqualTo(resource.ResourceId);
        await Assert.That(xml.ResourceId).IsEqualTo(resource.ResourceId);
        await Assert.That(yaml.ResourceId).IsEqualTo(resource.ResourceId);
        await Assert.That(source.ResourceId).IsEqualTo(resource.ResourceId);
        await Assert.That(projected.ResourceId).IsEqualTo(resource.ResourceId);
    }

    [Test]
    public async Task FileResourcesForTheSameNormalizedPathHaveTheSameIdentity()
    {
        var path = Path.Combine(Path.GetTempPath(), $"configlue-{Guid.NewGuid():N}.json");
        var first = new FileResource(path);
        var second = new FileResource(Path.Combine(Path.GetDirectoryName(path)!, ".", Path.GetFileName(path)));

        await Assert.That(first.ResourceId).IsEqualTo(second.ResourceId);
        await Assert.That(first.ResourceId.Value).StartsWith("file:");
    }
}
