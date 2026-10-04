using System.Text;
using Configlue.Provider.Yaml;
using Configlue.Testing;
using SharpYaml;

namespace Configlue.Tests;

/// <summary>Regression tests for issue #207: removed flow commas must not count as separators.</summary>
public sealed class YamlFlowMappingEditTests
{
    [Test]
    public async Task FlowMapping_RemoveLastAddNew_SavesValidYaml()
    {
        var updatedText = await WriteSectionAsync(
            "Settings: {a: 1, b: 2}\n",
            "{a: 1, c: 3}");
        var settings = GetSectionMapping(updatedText, "Settings");
        settings["a"]?.ToString().ShouldBe("1");
        settings["c"]?.ToString().ShouldBe("3");
        settings.ContainsKey("b").ShouldBeFalse();
    }

    [Test]
    public async Task FlowMapping_RemoveFirstAddNew_SavesValidYaml()
    {
        var updatedText = await WriteSectionAsync(
            "Settings: {a: 1, b: 2}\n",
            "{b: 2, c: 3}");
        var settings = GetSectionMapping(updatedText, "Settings");
        settings["b"]?.ToString().ShouldBe("2");
        settings["c"]?.ToString().ShouldBe("3");
        settings.ContainsKey("a").ShouldBeFalse();
    }

    [Test]
    public async Task FlowMapping_RemoveMiddleAddNew_SavesValidYaml()
    {
        var updatedText = await WriteSectionAsync(
            "Settings: {a: 1, b: 2, d: 3}\n",
            "{a: 1, d: 3, c: 4}");
        var settings = GetSectionMapping(updatedText, "Settings");
        settings["a"]?.ToString().ShouldBe("1");
        settings["d"]?.ToString().ShouldBe("3");
        settings["c"]?.ToString().ShouldBe("4");
        settings.ContainsKey("b").ShouldBeFalse();
    }

    [Test]
    public async Task FlowMapping_RemoveMultipleAddNew_SavesValidYaml()
    {
        var updatedText = await WriteSectionAsync(
            "Settings: {a: 1, b: 2, d: 3}\n",
            "{a: 1, c: 4}");
        var settings = GetSectionMapping(updatedText, "Settings");
        settings["a"]?.ToString().ShouldBe("1");
        settings["c"]?.ToString().ShouldBe("4");
        settings.ContainsKey("b").ShouldBeFalse();
        settings.ContainsKey("d").ShouldBeFalse();
    }

    [Test]
    public async Task FlowMapping_RemoveAllAddNew_SavesValidYaml()
    {
        var updatedText = await WriteSectionAsync(
            "Settings: {a: 1, b: 2}\n",
            "{c: 3}");
        var settings = GetSectionMapping(updatedText, "Settings");
        settings.Count.ShouldBe(1);
        settings["c"]?.ToString().ShouldBe("3");
    }

    [Test]
    public async Task FlowMapping_TrailingComma_RemoveLastAddNew_SavesValidYaml()
    {
        var updatedText = await WriteSectionAsync(
            "Settings: {a: 1, b: 2,}\n",
            "{a: 1, c: 3}");
        var settings = GetSectionMapping(updatedText, "Settings");
        settings["a"]?.ToString().ShouldBe("1");
        settings["c"]?.ToString().ShouldBe("3");
        settings.ContainsKey("b").ShouldBeFalse();
    }

    [Test]
    public async Task FlowMapping_CommentAndQuotedComma_RemoveLastAddNew_SavesValidYaml()
    {
        var updatedText = await WriteSectionAsync(
            "Settings: {a: \"1, x\", b: 2} # keep me\n",
            "{a: \"1, x\", c: 3}");
        updatedText.ShouldContain("# keep me");
        var settings = GetSectionMapping(updatedText, "Settings");
        settings["a"]?.ToString().ShouldBe("1, x");
        settings["c"]?.ToString().ShouldBe("3");
        settings.ContainsKey("b").ShouldBeFalse();
    }

    [Test]
    public async Task FlowMapping_Nested_RemoveLastAddNew_SavesValidYaml()
    {
        var updatedText = await WriteSectionAsync(
            "Settings: {outer: {a: 1, b: 2}, sibling: 1}\n",
            "{a: 1, c: 3}",
            "Settings:outer");
        var outer = GetSectionMapping(updatedText, "Settings", "outer");
        outer["a"]?.ToString().ShouldBe("1");
        outer["c"]?.ToString().ShouldBe("3");
        outer.ContainsKey("b").ShouldBeFalse();
    }

    [Test]
    public async Task FlowSequence_RemoveLastAddNew_SavesValidYaml()
    {
        var updatedText = await WriteSectionAsync(
            "Settings: [1, 2]\n",
            "[1, 3]");
        var root = YamlSerializer.Deserialize<object>(updatedText) as IDictionary<string, object?>
            ?? throw new InvalidOperationException("Root is not a mapping.");
        var items = root["Settings"] as IList<object?>
            ?? throw new InvalidOperationException("Settings is not a sequence.");
        items.Count.ShouldBe(2);
        items[0]?.ToString().ShouldBe("1");
        items[1]?.ToString().ShouldBe("3");
    }

    [Test]
    public async Task FlowMapping_RemoveLastAddNew_PreservesSiblingCommentAndUnknown()
    {
        var initial =
            "# root comment\n"
            + "Settings: {a: 1, b: 2}\n"
            + "Sibling: keep\n"
            + "Unknown: {x: 1} # trailing\n";
        var updatedText = await WriteSectionAsync(initial, "{a: 1, c: 3}");
        updatedText.ShouldContain("# root comment");
        updatedText.ShouldContain("Sibling: keep");
        updatedText.ShouldContain("# trailing");
        var root = YamlSerializer.Deserialize<object>(updatedText) as IDictionary<string, object?>
            ?? throw new InvalidOperationException("Root is not a mapping.");
        (root["Unknown"] as IDictionary<string, object?>)!["x"]?.ToString().ShouldBe("1");
        var settings = GetSectionMapping(updatedText, "Settings");
        settings["a"]?.ToString().ShouldBe("1");
        settings["c"]?.ToString().ShouldBe("3");
        settings.ContainsKey("b").ShouldBeFalse();
    }

    private static async Task<string> WriteSectionAsync(
        string initialDocument,
        string updatedSection,
        string sectionPath = "Settings")
    {
        var resource = new InMemoryResource();
        await resource.WriteAsync(
            new ResourceWriteRequest(Encoding.UTF8.GetBytes(initialDocument)));
        var section = new YamlSectionResource(resource, sectionPath);
        await section.WriteAsync(
            new ResourceWriteRequest(Encoding.UTF8.GetBytes(updatedSection)));

        // The saved physical document must re-parse as valid YAML (not string comparison only).
        var updatedText = Encoding.UTF8.GetString((await resource.ReadAsync()).Content.Span);
        YamlSerializer.Deserialize<object>(updatedText).ShouldNotBeNull();

        // The same resource must remain readable through the section view.
        var reread = await section.ReadAsync();
        reread.Status.ShouldBe(StateReadStatus.Success);
        YamlSerializer.Deserialize<object>(Encoding.UTF8.GetString(reread.Content.Span))
            .ShouldNotBeNull();

        return updatedText;
    }

    private static IDictionary<string, object?> GetSectionMapping(
        string document,
        params string[] path)
    {
        object? current = YamlSerializer.Deserialize<object>(document);
        foreach (var key in path)
        {
            current = (current as IDictionary<string, object?>
                ?? throw new InvalidOperationException("Expected a YAML mapping."))[key];
        }

        return current as IDictionary<string, object?>
            ?? throw new InvalidOperationException("Section is not a mapping.");
    }
}
