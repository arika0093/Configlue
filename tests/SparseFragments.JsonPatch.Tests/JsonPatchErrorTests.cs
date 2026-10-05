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
            }
        );

    private static global::Configlue.Optional<ConfigluePatchWidget.Fragment?> ConfiglueBaseline() =>
        global::Configlue.Optional<ConfigluePatchWidget.Fragment?>.Present(
            new ConfigluePatchWidget.Fragment
            {
                Count = global::Configlue.Optional<int>.Present(1),
                Tags = global::Configlue.Optional<List<string>>.Present(new List<string> { "a" }),
            }
        );

    private static global::SparseFragments.JsonPatchException ImportStandalone(string patch) =>
        Should.Throw<global::SparseFragments.JsonPatchException>(() =>
            PatchWidget.Patch.FromJsonPatch(StandaloneBaseline(), Utf8(patch))
        );

    private static global::Configlue.JsonPatchException ImportConfiglue(string patch) =>
        Should.Throw<global::Configlue.JsonPatchException>(() =>
            ConfigluePatchWidget.Patch.FromJsonPatch(ConfiglueBaseline(), Utf8(patch))
        );

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
            if (standalone)
            {
                ImportStandalone(json)
                    .Kind.ShouldBe(global::SparseFragments.JsonPatchErrorKind.MalformedDocument);
            }
            else
            {
                ImportConfiglue(json)
                    .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MalformedDocument);
            }
        }

        // Empty bytes are also malformed.
        if (standalone)
        {
            Should
                .Throw<global::SparseFragments.JsonPatchException>(() =>
                    PatchWidget.Patch.FromJsonPatch(StandaloneBaseline(), Array.Empty<byte>())
                )
                .Kind.ShouldBe(global::SparseFragments.JsonPatchErrorKind.MalformedDocument);
        }
        else
        {
            Should
                .Throw<global::Configlue.JsonPatchException>(() =>
                    ConfigluePatchWidget.Patch.FromJsonPatch(
                        ConfiglueBaseline(),
                        Array.Empty<byte>()
                    )
                )
                .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MalformedDocument);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void UnknownOperation(bool standalone)
    {
        if (standalone)
        {
            ImportStandalone("""[{"op":"merge","path":"/Count","value":1}]""")
                .Kind.ShouldBe(global::SparseFragments.JsonPatchErrorKind.UnknownOperation);
        }
        else
        {
            ImportConfiglue("""[{"op":"merge","path":"/Count","value":1}]""")
                .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.UnknownOperation);
        }
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
            if (standalone)
            {
                ImportStandalone(json)
                    .Kind.ShouldBe(global::SparseFragments.JsonPatchErrorKind.MalformedPointer);
            }
            else
            {
                ImportConfiglue(json)
                    .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MalformedPointer);
            }
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
            if (standalone)
            {
                ImportStandalone(json)
                    .Kind.ShouldBe(global::SparseFragments.JsonPatchErrorKind.MissingTarget);
            }
            else
            {
                ImportConfiglue(json)
                    .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MissingTarget);
            }
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
            if (standalone)
            {
                ImportStandalone(json)
                    .Kind.ShouldBe(global::SparseFragments.JsonPatchErrorKind.MissingParent);
            }
            else
            {
                ImportConfiglue(json)
                    .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MissingParent);
            }
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
            if (standalone)
            {
                ImportStandalone(json)
                    .Kind.ShouldBe(global::SparseFragments.JsonPatchErrorKind.InvalidArrayIndex);
            }
            else
            {
                ImportConfiglue(json)
                    .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.InvalidArrayIndex);
            }
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void FailedTest(bool standalone)
    {
        if (standalone)
        {
            ImportStandalone("""[{"op":"test","path":"/Count","value":999}]""")
                .Kind.ShouldBe(global::SparseFragments.JsonPatchErrorKind.TestFailed);
        }
        else
        {
            ImportConfiglue("""[{"op":"test","path":"/Count","value":999}]""")
                .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.TestFailed);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void UnmappedProperty(bool standalone)
    {
        if (standalone)
        {
            ImportStandalone("""[{"op":"add","path":"/Unknown","value":1}]""")
                .Kind.ShouldBe(global::SparseFragments.JsonPatchErrorKind.UnmappedProperty);
        }
        else
        {
            ImportConfiglue("""[{"op":"add","path":"/Unknown","value":1}]""")
                .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.UnmappedProperty);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void DeserializationTypeMismatch(bool standalone)
    {
        if (standalone)
        {
            ImportStandalone("""[{"op":"replace","path":"/Count","value":"not-a-number"}]""")
                .Kind.ShouldBe(global::SparseFragments.JsonPatchErrorKind.DeserializationFailed);
        }
        else
        {
            ImportConfiglue("""[{"op":"replace","path":"/Count","value":"not-a-number"}]""")
                .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.DeserializationFailed);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void MoveFromMissingTarget(bool standalone)
    {
        if (standalone)
        {
            ImportStandalone("""[{"op":"move","from":"/Missing","path":"/Count"}]""")
                .Kind.ShouldBe(global::SparseFragments.JsonPatchErrorKind.MissingTarget);
        }
        else
        {
            ImportConfiglue("""[{"op":"move","from":"/Missing","path":"/Count"}]""")
                .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MissingTarget);
        }
    }
}
