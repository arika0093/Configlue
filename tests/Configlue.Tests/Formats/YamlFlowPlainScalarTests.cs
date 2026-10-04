using System.Text;
using Configlue.Provider.Yaml;
using Configlue.Testing;
using SharpYaml;

namespace Configlue.Tests;

/// <summary>Regression tests for issue #215: plain scalars containing
/// apostrophes or hashes must not break flow collection editing.</summary>
public sealed class YamlFlowPlainScalarTests
{
    [Test]
    public async Task FlowMapping_PreservesPlainScalarAndAddsEntry()
    {
        foreach (var scalar in new[] { "can't", "a#b", "normal" })
        {
            var resource = new InMemoryResource();
            await resource.WriteAsync(
                new ResourceWriteRequest(Encoding.UTF8.GetBytes($"Settings: {{text: {scalar}}}\n"))
            );
            var section = new YamlSectionResource(resource, "Settings");
            (await section.ReadAsync()).Status.ShouldBe(StateReadStatus.Success);

            await section.WriteAsync(
                new ResourceWriteRequest(Encoding.UTF8.GetBytes($"{{text: {scalar}, extra: 1}}"))
            );

            var fileText = Encoding.UTF8.GetString((await resource.ReadAsync()).Content.Span);
            fileText.ShouldContain($"text: {scalar}");
            fileText.ShouldContain("extra: 1");

            var reread = await section.ReadAsync();
            reread.Status.ShouldBe(StateReadStatus.Success);
            var values =
                (IDictionary<string, object?>)
                    YamlSerializer.Deserialize<object>(
                        Encoding.UTF8.GetString(reread.Content.Span)
                    )!;
            values["text"]!.ToString().ShouldBe(scalar);
            values["extra"]!.ToString().ShouldBe("1");
        }
    }

    [Test]
    public async Task NestedFlowAndSequence_PreservePlainScalars()
    {
        var resource = new InMemoryResource();
        await resource.WriteAsync(
            new ResourceWriteRequest(
                Encoding.UTF8.GetBytes(
                    "Settings: {a: {x: can't, y: a#b}, b: [can't, a#b, normal]}\n"
                )
            )
        );
        var section = new YamlSectionResource(resource, "Settings");
        (await section.ReadAsync()).Status.ShouldBe(StateReadStatus.Success);

        await section.WriteAsync(
            new ResourceWriteRequest(
                Encoding.UTF8.GetBytes("{a: {x: can't, y: a#b}, b: [can't, a#b, normal], c: 2}")
            )
        );

        var reread = await section.ReadAsync();
        reread.Status.ShouldBe(StateReadStatus.Success);
        var text = Encoding.UTF8.GetString(reread.Content.Span);
        text.ShouldContain("can't");
        text.ShouldContain("a#b");
        text.ShouldContain("c: 2");
    }

    [Test]
    public async Task MultilineFlow_PreservesPlainScalars()
    {
        var resource = new InMemoryResource();
        await resource.WriteAsync(
            new ResourceWriteRequest(
                Encoding.UTF8.GetBytes("Settings: {\n  text: can't,\n  other: a#b\n}\n")
            )
        );
        var section = new YamlSectionResource(resource, "Settings");

        await section.WriteAsync(
            new ResourceWriteRequest(Encoding.UTF8.GetBytes("{text: can't, other: a#b, extra: 1}"))
        );

        var reread = await section.ReadAsync();
        reread.Status.ShouldBe(StateReadStatus.Success);
        var values =
            (IDictionary<string, object?>)
                YamlSerializer.Deserialize<object>(Encoding.UTF8.GetString(reread.Content.Span))!;
        values["text"]!.ToString().ShouldBe("can't");
        values["other"]!.ToString().ShouldBe("a#b");
        values["extra"]!.ToString().ShouldBe("1");
    }

    [Test]
    public async Task QuotedScalarsAndRealComments_AreNotSplit()
    {
        var resource = new InMemoryResource();
        await resource.WriteAsync(
            new ResourceWriteRequest(
                Encoding.UTF8.GetBytes(
                    "Settings: {single: 'a,b}c # not comment', double: \"d,e}f # not comment\", escaped: 'it''s', other: 1, # real } comment , with brackets\n}\n"
                )
            )
        );
        var section = new YamlSectionResource(resource, "Settings");
        (await section.ReadAsync()).Status.ShouldBe(StateReadStatus.Success);

        await section.WriteAsync(
            new ResourceWriteRequest(
                Encoding.UTF8.GetBytes(
                    "{single: 'a,b}c # not comment', double: \"d,e}f # not comment\", escaped: 'it''s', other: 1, extra: 2}"
                )
            )
        );

        var reread = await section.ReadAsync();
        reread.Status.ShouldBe(StateReadStatus.Success);
        var values =
            (IDictionary<string, object?>)
                YamlSerializer.Deserialize<object>(Encoding.UTF8.GetString(reread.Content.Span))!;
        values["single"]!.ToString().ShouldBe("a,b}c # not comment");
        values["double"]!.ToString().ShouldBe("d,e}f # not comment");
        values["escaped"]!.ToString().ShouldBe("it's");
        values["other"]!.ToString().ShouldBe("1");
        values["extra"]!.ToString().ShouldBe("2");
    }

    [Test]
    public async Task RemovingFlowEntry_WithPlainScalar_KeepsSiblings()
    {
        var resource = new InMemoryResource();
        await resource.WriteAsync(
            new ResourceWriteRequest(Encoding.UTF8.GetBytes("Settings: {text: can't, gone: 1}\n"))
        );
        var section = new YamlSectionResource(resource, "Settings");

        await section.WriteAsync(new ResourceWriteRequest(Encoding.UTF8.GetBytes("{text: can't}")));

        var reread = await section.ReadAsync();
        reread.Status.ShouldBe(StateReadStatus.Success);
        Encoding.UTF8.GetString(reread.Content.Span).ShouldContain("can't");
    }

    [Test]
    public async Task RemovingBlockEntry_PreservesInlineCommentAfterApostrophe()
    {
        var resource = new InMemoryResource();
        await resource.WriteAsync(
            new ResourceWriteRequest(
                Encoding.UTF8.GetBytes("Settings:\n  text: can't # keep me\n  other: 1\n")
            )
        );
        var section = new YamlSectionResource(resource, "Settings");

        await section.WriteAsync(new ResourceWriteRequest(Encoding.UTF8.GetBytes("{other: 1}")));

        var fileText = Encoding.UTF8.GetString((await resource.ReadAsync()).Content.Span);
        fileText.ShouldContain("# keep me");
        fileText.ShouldContain("other: 1");
    }

    [Test]
    public async Task RemovingBlockEntry_WithHashInPlainScalar_LeavesNoStrayComment()
    {
        var resource = new InMemoryResource();
        await resource.WriteAsync(
            new ResourceWriteRequest(Encoding.UTF8.GetBytes("Settings:\n  text: a#b\n  other: 1\n"))
        );
        var section = new YamlSectionResource(resource, "Settings");

        await section.WriteAsync(new ResourceWriteRequest(Encoding.UTF8.GetBytes("{other: 1}")));

        var fileText = Encoding.UTF8.GetString((await resource.ReadAsync()).Content.Span);
        fileText.ShouldNotContain("#b");
        fileText.ShouldContain("other: 1");
    }
}
