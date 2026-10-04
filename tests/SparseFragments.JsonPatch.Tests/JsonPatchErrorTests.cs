using SparseFragments;
using static SparseFragments.JsonPatch.Tests.PatchTestHelpers;

namespace SparseFragments.JsonPatch.Tests;

/// <summary>Error-kind parity between standalone and Configlue import bridges.</summary>
public sealed class JsonPatchErrorTests
{
    private static Optional<PatchWidget.Fragment?> StandaloneBaseline() =>
        Optional<PatchWidget.Fragment?>.Present(
            new PatchWidget.Fragment
            {
                Count = Optional<int>.Present(1),
                Tags = Optional<List<string>>.Present(new List<string> { "a" }),
            });

    private static global::Configlue.Optional<ConfigluePatchWidget.Fragment?> ConfiglueBaseline() =>
        global::Configlue.Optional<ConfigluePatchWidget.Fragment?>.Present(
            new ConfigluePatchWidget.Fragment
            {
                Count = global::Configlue.Optional<int>.Present(1),
                Tags = global::Configlue.Optional<List<string>>.Present(new List<string> { "a" }),
            });

    private static JsonPatchException ImportStandalone(string patch) =>
        Should.Throw<JsonPatchException>(() =>
            PatchWidget.Patch.FromJsonPatch(StandaloneBaseline(), Utf8(patch)));

    private static JsonPatchException ImportConfiglue(string patch) =>
        Should.Throw<JsonPatchException>(() =>
            ConfigluePatchWidget.Patch.FromJsonPatch(ConfiglueBaseline(), Utf8(patch)));

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void MalformedDocument(bool standalone)
    {
        var cases = new[]
        {
            """{"op":"add","path":"/Count","value":1}""",
            """[{"op":"add"}]""",
            """[{"op":"add","path":"/Count"}]""",
            """[{"op":"move","path":"/Count"}]""",
            """not json""",
            """[42]""",
        };
        foreach (var json in cases)
        {
            var kind = standalone
                ? ImportStandalone(json).Kind
                : ImportConfiglue(json).Kind;
            kind.ShouldBe(JsonPatchErrorKind.MalformedDocument);
        }

        // Empty bytes are also malformed.
        var emptyKind = standalone
            ? Should.Throw<JsonPatchException>(() =>
                PatchWidget.Patch.FromJsonPatch(
                    StandaloneBaseline(),
                    Array.Empty<byte>())).Kind
            : Should.Throw<JsonPatchException>(() =>
                ConfigluePatchWidget.Patch.FromJsonPatch(
                    ConfiglueBaseline(),
                    Array.Empty<byte>())).Kind;
        emptyKind.ShouldBe(JsonPatchErrorKind.MalformedDocument);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void UnknownOperation(bool standalone)
    {
        var kind = standalone
            ? ImportStandalone("""[{"op":"merge","path":"/Count","value":1}]""").Kind
            : ImportConfiglue("""[{"op":"merge","path":"/Count","value":1}]""").Kind;
        kind.ShouldBe(JsonPatchErrorKind.UnknownOperation);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void MalformedPointer(bool standalone)
    {
        var cases = new[]
        {
            """[{"op":"remove","path":"Count"}]""",
            """[{"op":"remove","path":"/a~2b"}]""",
            """[{"op":"remove","path":"/dangling~"}]""",
        };
        foreach (var json in cases)
        {
            var kind = standalone
                ? ImportStandalone(json).Kind
                : ImportConfiglue(json).Kind;
            kind.ShouldBe(JsonPatchErrorKind.MalformedPointer);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void MissingTarget(bool standalone)
    {
        var cases = new[]
        {
            """[{"op":"remove","path":"/Missing"}]""",
            """[{"op":"replace","path":"/Missing","value":1}]""",
            """[{"op":"test","path":"/Missing","value":1}]""",
            """[{"op":"remove","path":"/Nested/Missing"}]""",
        };
        foreach (var json in cases)
        {
            var kind = standalone
                ? ImportStandalone(json).Kind
                : ImportConfiglue(json).Kind;
            kind.ShouldBe(JsonPatchErrorKind.MissingTarget);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void MissingParentForNestedAdd(bool standalone)
    {
        var cases = new[]
        {
            """[{"op":"add","path":"/Missing/Child","value":1}]""",
            """[{"op":"add","path":"/Nested/Missing/Deep","value":1}]""",
        };
        foreach (var json in cases)
        {
            var kind = standalone
                ? ImportStandalone(json).Kind
                : ImportConfiglue(json).Kind;
            kind.ShouldBe(JsonPatchErrorKind.MissingParent);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void InvalidArrayIndex(bool standalone)
    {
        var cases = new[]
        {
            """[{"op":"remove","path":"/Tags/5"}]""",
            """[{"op":"remove","path":"/Tags/-"}]""",
            """[{"op":"remove","path":"/Tags/nope"}]""",
            """[{"op":"replace","path":"/Tags/01","value":"x"}]""",
        };
        foreach (var json in cases)
        {
            var kind = standalone
                ? ImportStandalone(json).Kind
                : ImportConfiglue(json).Kind;
            kind.ShouldBe(JsonPatchErrorKind.InvalidArrayIndex);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void FailedTest(bool standalone)
    {
        var kind = standalone
            ? ImportStandalone("""[{"op":"test","path":"/Count","value":999}]""").Kind
            : ImportConfiglue("""[{"op":"test","path":"/Count","value":999}]""").Kind;
        kind.ShouldBe(JsonPatchErrorKind.TestFailed);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void UnmappedProperty(bool standalone)
    {
        var kind = standalone
            ? ImportStandalone("""[{"op":"add","path":"/Unknown","value":1}]""").Kind
            : ImportConfiglue("""[{"op":"add","path":"/Unknown","value":1}]""").Kind;
        kind.ShouldBe(JsonPatchErrorKind.UnmappedProperty);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void DeserializationTypeMismatch(bool standalone)
    {
        var kind = standalone
            ? ImportStandalone("""[{"op":"replace","path":"/Count","value":"not-a-number"}]""").Kind
            : ImportConfiglue("""[{"op":"replace","path":"/Count","value":"not-a-number"}]""").Kind;
        kind.ShouldBe(JsonPatchErrorKind.DeserializationFailed);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void MoveFromMissingTarget(bool standalone)
    {
        var kind = standalone
            ? ImportStandalone("""[{"op":"move","from":"/Missing","path":"/Count"}]""").Kind
            : ImportConfiglue("""[{"op":"move","from":"/Missing","path":"/Count"}]""").Kind;
        kind.ShouldBe(JsonPatchErrorKind.MissingTarget);
    }
}
