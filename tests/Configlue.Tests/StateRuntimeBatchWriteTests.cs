using Configlue.Extensions.MSOptions;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue.Tests;

public sealed partial class StateRuntimeTests
{
    [Test]
    public async Task ApplyPatchesAsync_GroupsSiblingJsonSectionsIntoOnePhysicalWrite()
    {
        var resource = new InMemoryResource();
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var firstSection = new JsonSectionResource(resource, "App:First");
        var secondSection = new JsonSectionResource(resource, "App:Second");
        var firstSource = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "first",
            firstSection,
            codec,
            priority: 10
        );
        var secondSource = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "second",
            secondSection,
            codec,
            priority: 0
        );
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([firstSource, secondSource])
        );

        var result = await options.ApplyPatchesAsync([
            new StateSourcePatch(
                "first",
                new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }
            ),
            new StateSourcePatch(
                "second",
                new AppSettings.Patch { Label = FragmentOperation<string?>.Set("second section") }
            ),
        ]);
        var first = await firstSource.Reader.ReadAsync();
        var second = await secondSource.Reader.ReadAsync();
        var stored = await resource.ReadAsync();

        (result.Sources.Count).ShouldBe(2);
        (result.PhysicalWriteCount).ShouldBe(1);
        (resource.WriteCount).ShouldBe(1);
        (result.Sources.Select(static item => item.Revision).Distinct().Count()).ShouldBe(1);
        (first.Value!.RetryCount.Value).ShouldBe(7);
        (second.Value!.Label.Value).ShouldBe("second section");
        (stored.Status).ShouldBe(StateReadStatus.Success);
    }

    [Test]
    public async Task ApplyPatchesAsync_PersistsGroupedSectionsThroughFileResource()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"configlue-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var resource = new FileResource(Path.Combine(directory, "settings.json"));
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var first = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "first",
            new JsonSectionResource(resource, "App:First"),
            codec
        );
        var second = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "second",
            new JsonSectionResource(resource, "App:Second"),
            codec
        );
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([first, second])
        );

        try
        {
            var result = await options.ApplyPatchesAsync([
                new StateSourcePatch(
                    "first",
                    new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(5) }
                ),
                new StateSourcePatch(
                    "second",
                    new AppSettings.Patch { Label = FragmentOperation<string?>.Set("file batch") }
                ),
            ]);
            var firstState = await first.Reader.ReadAsync();
            var secondState = await second.Reader.ReadAsync();

            (result.PhysicalWriteCount).ShouldBe(1);
            (firstState.Value!.RetryCount.Value).ShouldBe(5);
            (secondState.Value!.Label.Value).ShouldBe("file batch");
            (firstState.Revision).ShouldBe(secondState.Revision);
        }
        finally
        {
            resource.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task ApplyPatchesAsync_RejectsOverlappingResourceScopesBeforeWriting()
    {
        var resource = new InMemoryResource();
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var parent = new JsonSectionResource(resource, "App");
        var child = new JsonSectionResource(resource, "App:Child");
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                SerializedStateSource.FromResource<AppSettings.Fragment>(
                    "parent",
                    parent,
                    codec,
                    priority: 10
                ),
                SerializedStateSource.FromResource<AppSettings.Fragment>(
                    "child",
                    child,
                    codec,
                    priority: 0
                ),
            ])
        );
        var failed = false;
        try
        {
            await options.ApplyPatchesAsync([
                new StateSourcePatch(
                    "parent",
                    new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }
                ),
                new StateSourcePatch(
                    "child",
                    new AppSettings.Patch { Label = FragmentOperation<string?>.Set("child") }
                ),
            ]);
        }
        catch (StateConflictException)
        {
            failed = true;
        }

        (failed).ShouldBeTrue();
        (resource.WriteCount).ShouldBe(0);

        var json = new JsonSectionResource(resource, "App:Json");
        var xml = new XmlSectionResource(resource, "App:Xml");
        var differentDomains = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                SerializedStateSource.FromResource<AppSettings.Fragment>("json", json, codec),
                SerializedStateSource.FromResource<AppSettings.Fragment>(
                    "xml",
                    xml,
                    new XmlStateCodec<AppSettings.Fragment>()
                ),
            ])
        );
        var domainConflict = false;
        try
        {
            await differentDomains.ApplyPatchesAsync([
                new StateSourcePatch(
                    "json",
                    new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(3) }
                ),
                new StateSourcePatch(
                    "xml",
                    new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(4) }
                ),
            ]);
        }
        catch (NotSupportedException)
        {
            domainConflict = true;
        }

        (domainConflict).ShouldBeTrue();
        (resource.WriteCount).ShouldBe(0);
    }

    [Test]
    public async Task ApplyPatchesAsync_BatchesXmlAndYamlSectionUpdates()
    {
        var xmlResource = new InMemoryResource();
        var xmlCodec = new XmlStateCodec<AppSettings.Fragment>();
        var xmlFirst = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "xml-first",
            new XmlSectionResource(xmlResource, "App:First"),
            xmlCodec
        );
        var xmlSecond = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "xml-second",
            new XmlSectionResource(xmlResource, "App:Second"),
            xmlCodec
        );
        var xmlOptions = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([xmlFirst, xmlSecond])
        );
        var xmlResult = await xmlOptions.ApplyPatchesAsync([
            new StateSourcePatch(
                "xml-first",
                new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(6) }
            ),
            new StateSourcePatch(
                "xml-second",
                new AppSettings.Patch { Label = FragmentOperation<string?>.Set("xml") }
            ),
        ]);

        var yamlResource = new InMemoryResource();
        var yamlCodec = new YamlStateCodec<AppSettings.Fragment>(
            modelSchema: AppSettings.FragmentSchema
        );
        var yamlFirst = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "yaml-first",
            new YamlSectionResource(yamlResource, "App:First"),
            yamlCodec
        );
        var yamlSecond = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "yaml-second",
            new YamlSectionResource(yamlResource, "App:Second"),
            yamlCodec
        );
        var yamlOptions = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([yamlFirst, yamlSecond])
        );
        var yamlResult = await yamlOptions.ApplyPatchesAsync([
            new StateSourcePatch(
                "yaml-first",
                new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(8) }
            ),
            new StateSourcePatch(
                "yaml-second",
                new AppSettings.Patch { Label = FragmentOperation<string?>.Set("yaml") }
            ),
        ]);

        (xmlResult.PhysicalWriteCount).ShouldBe(1);
        (xmlResource.WriteCount).ShouldBe(1);
        ((await xmlFirst.Reader.ReadAsync()).Value!.RetryCount.Value).ShouldBe(6);
        ((await xmlSecond.Reader.ReadAsync()).Value!.Label.Value).ShouldBe("xml");
        (yamlResult.PhysicalWriteCount).ShouldBe(1);
        (yamlResource.WriteCount).ShouldBe(1);
        ((await yamlFirst.Reader.ReadAsync()).Value!.RetryCount.Value).ShouldBe(8);
        ((await yamlSecond.Reader.ReadAsync()).Value!.Label.Value).ShouldBe("yaml");
    }
}
