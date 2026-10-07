using System.Text.Json.Nodes;
using Configlue;
using static SparseFragments.JsonPatch.Tests.PatchTestHelpers;

namespace SparseFragments.JsonPatch.Tests;

// Canonical product-neutral RFC 6902 behavioral suite for the Configlue-owned
// JSON Patch runtime (global::Configlue.JsonPatchDocument/JsonPatchEngine/
// JsonPointer/ConfiglueJsonPatch). No generated models: every test exercises the
// runtime directly so this suite also defines the parity contract the upstream
// SparseFragments runtime must satisfy for a future single-implementation
// cutover. Configlue-specific coverage (generated FromJsonPatch/ToJsonPatch
// models, public error/API adaptation) lives in ConfiglueJsonPatchTests and
// ConfiglueJsonPatchAdapterTests.
public sealed class JsonPatchBehavioralTests
{
    private static JsonNode Node(string json) => JsonNode.Parse(json)!;

    private static global::Configlue.JsonPatchDocument Doc(string json) =>
        global::Configlue.JsonPatchDocument.Parse(Utf8(json));

    private static string AppliedJson(string baseline, string patch)
    {
        var result = global::Configlue.JsonPatchEngine.Apply(
            Node(baseline),
            false,
            Doc(patch)
        );
        return result.Node!.ToJsonString();
    }

    private static global::Configlue.JsonPatchException ApplyFailure(string baseline, string patch) =>
        Should.Throw<global::Configlue.JsonPatchException>(() =>
            global::Configlue.JsonPatchEngine.Apply(Node(baseline), false, Doc(patch))
        );

    [Test]
    public void PointerRootParsesToNoTokens()
    {
        global::Configlue.JsonPointer.Parse(string.Empty).ShouldBeEmpty();
        global::Configlue.JsonPointer.Format(Array.Empty<string>()).ShouldBe(string.Empty);
    }

    [Test]
    public void PointerEscapeRoundTrip()
    {
        var tokens = global::Configlue.JsonPointer.Parse("/a~1b/m~0n/plain");
        tokens.ShouldBe(new[] { "a/b", "m~n", "plain" });
        global::Configlue.JsonPointer.Format(tokens).ShouldBe("/a~1b/m~0n/plain");
        global::Configlue.JsonPointer.Escape("a/b").ShouldBe("a~1b");
        global::Configlue.JsonPointer.Escape("m~n").ShouldBe("m~0n");
    }

    [Test]
    public void PointerRejectsMissingLeadingSlash()
    {
        var exception = Should.Throw<global::Configlue.JsonPatchException>(() =>
            global::Configlue.JsonPointer.Parse("a")
        );
        exception.Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MalformedPointer);
    }

    [Test]
    public void PointerRejectsBadEscapes()
    {
        foreach (var pointer in new[] { "/a~", "/a~2b", "/~" })
        {
            var exception = Should.Throw<global::Configlue.JsonPatchException>(() =>
                global::Configlue.JsonPointer.Parse(pointer)
            );
            exception.Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MalformedPointer);
        }
    }

    [Test]
    public void ParseRejectsMalformedDocuments()
    {
        // Empty bytes, invalid JSON, and non-array roots are document errors.
        Should
            .Throw<global::Configlue.JsonPatchException>(() =>
                global::Configlue.JsonPatchDocument.Parse(Array.Empty<byte>())
            )
            .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MalformedDocument);
        Should
            .Throw<global::Configlue.JsonPatchException>(() =>
                global::Configlue.JsonPatchDocument.Parse("{not json")
            )
            .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MalformedDocument);
        Should
            .Throw<global::Configlue.JsonPatchException>(() =>
                global::Configlue.JsonPatchDocument.Parse("{}")
            )
            .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MalformedDocument);
        Should
            .Throw<global::Configlue.JsonPatchException>(() =>
                global::Configlue.JsonPatchDocument.Parse("[42]")
            )
            .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MalformedDocument);
    }

    [Test]
    public void ParseRejectsMalformedOperations()
    {
        // Unknown op names surface distinctly from malformed shapes.
        Should
            .Throw<global::Configlue.JsonPatchException>(() =>
                Doc("""[{"op":"merge","path":"/a"}]""")
            )
            .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.UnknownOperation);

        foreach (
            var patch in new[]
            {
                """[{"path":"/a"}]""",
                """[{"op":"add"}]""",
                """[{"op":"add","path":"/a"}]""",
                """[{"op":"remove","path":"/a","from":42}]""",
                """[{"op":"move","path":"/a"}]""",
                """[{"op":"copy","path":"/a"}]""",
            }
        )
        {
            Should
                .Throw<global::Configlue.JsonPatchException>(() => Doc(patch))
                .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MalformedDocument);
        }
    }

    [Test]
    public void ParseSurfacesMalformedPointersDistinctly()
    {
        // Pointer syntax errors are MalformedPointer, not MalformedDocument.
        Should
            .Throw<global::Configlue.JsonPatchException>(() =>
                Doc("""[{"op":"add","path":"a","value":1}]""")
            )
            .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MalformedPointer);
        Should
            .Throw<global::Configlue.JsonPatchException>(() =>
                Doc("""[{"op":"move","from":"/a~","path":"/b"}]""")
            )
            .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MalformedPointer);
    }

    [Test]
    public void AddReplaceRemoveObjectMember()
    {
        AppliedJson("""{"a":1}""", """[{"op":"add","path":"/b","value":2}]""")
            .ShouldBe("""{"a":1,"b":2}""");
        AppliedJson("""{"a":1}""", """[{"op":"replace","path":"/a","value":9}]""")
            .ShouldBe("""{"a":9}""");
        AppliedJson("""{"a":1,"b":2}""", """[{"op":"remove","path":"/a"}]""")
            .ShouldBe("""{"b":2}""");
    }

    [Test]
    public void RootTransitions()
    {
        AppliedJson("""{"a":1}""", """[{"op":"replace","path":"","value":{"b":2}}]""")
            .ShouldBe("""{"b":2}""");

        var removed = global::Configlue.JsonPatchEngine.Apply(
            Node("""{"a":1}"""),
            false,
            Doc("""[{"op":"remove","path":""}]""")
        );
        removed.IsAbsent.ShouldBeTrue();

        var added = global::Configlue.JsonPatchEngine.Apply(
            null,
            true,
            Doc("""[{"op":"add","path":"","value":{"b":2}}]""")
        );
        added.IsAbsent.ShouldBeFalse();
        added.Node!.ToJsonString().ShouldBe("""{"b":2}""");
    }

    [Test]
    public void AddNestedMissingParent()
    {
        ApplyFailure("""{"a":1}""", """[{"op":"add","path":"/missing/child","value":1}]""")
            .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MissingParent);
    }

    [Test]
    public void ArrayInsertAppendRemove()
    {
        AppliedJson("""{"t":["a","b"]}""", """[{"op":"add","path":"/t/1","value":"x"}]""")
            .ShouldBe("""{"t":["a","x","b"]}""");
        AppliedJson("""{"t":["a"]}""", """[{"op":"add","path":"/t/-","value":"z"}]""")
            .ShouldBe("""{"t":["a","z"]}""");
        AppliedJson("""{"t":["a","b"]}""", """[{"op":"remove","path":"/t/0"}]""")
            .ShouldBe("""{"t":["b"]}""");
    }

    [Test]
    public void ArrayIndexValidation()
    {
        foreach (
            var patch in new[]
            {
                """[{"op":"add","path":"/t/01","value":"x"}]""",
                """[{"op":"replace","path":"/t/5","value":"x"}]""",
                """[{"op":"remove","path":"/t/-"}]""",
                """[{"op":"replace","path":"/t/-","value":"x"}]""",
                """[{"op":"add","path":"/t/x","value":"x"}]""",
            }
        )
        {
            ApplyFailure("""{"t":["a"]}""", patch)
                .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.InvalidArrayIndex);
        }
    }

    [Test]
    public void MoveShiftsArrayIndices()
    {
        AppliedJson("""{"t":["a","b","c"]}""", """[{"op":"move","from":"/t/0","path":"/t/1"}]""")
            .ShouldBe("""{"t":["b","a","c"]}""");
    }

    [Test]
    public void MoveIntoOwnChildIsRejected()
    {
        // RFC 6902 section 4.6: 'from' must not be a proper prefix of 'path'.
        ApplyFailure("""{"a":{"b":1}}""", """[{"op":"move","from":"/a","path":"/a/b"}]""")
            .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MalformedPointer);
        ApplyFailure("""{"a":1}""", """[{"op":"move","from":"","path":"/a"}]""")
            .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MalformedPointer);
    }

    [Test]
    public void CopyDuplicatesValue()
    {
        AppliedJson("""{"a":1}""", """[{"op":"copy","from":"/a","path":"/b"}]""")
            .ShouldBe("""{"a":1,"b":1}""");
        ApplyFailure("""{"a":1}""", """[{"op":"copy","from":"/missing","path":"/b"}]""")
            .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MissingTarget);
    }

    [Test]
    public void TestUsesNumericEquality()
    {
        // 1 and 1.0 are numerically equal per RFC 6902 section 4.6.
        AppliedJson("""{"n":1}""", """[{"op":"test","path":"/n","value":1.0}]""")
            .ShouldBe("""{"n":1}""");
        ApplyFailure("""{"n":1}""", """[{"op":"test","path":"/n","value":2}]""")
            .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.TestFailed);
    }

    [Test]
    public void TestIgnoresObjectMemberOrder()
    {
        AppliedJson(
            """{"o":{"a":1,"b":[1,2]}}""",
            """[{"op":"test","path":"/o","value":{"b":[1,2],"a":1}}]"""
        ).ShouldBe("""{"o":{"a":1,"b":[1,2]}}""");
    }

    [Test]
    public void FailedApplyLeavesBaselineUntouched()
    {
        var baseline = Node("""{"a":1}""");
        Should.Throw<global::Configlue.JsonPatchException>(() =>
            global::Configlue.JsonPatchEngine.Apply(
                baseline,
                false,
                Doc(
                    """[{"op":"replace","path":"/a","value":5},{"op":"remove","path":"/missing"}]"""
                )
            )
        );
        baseline.ToJsonString().ShouldBe("""{"a":1}""");
    }

    [Test]
    public void PropertyNameComparisonIsHonored()
    {
        var patch = Doc("""[{"op":"replace","path":"/COUNT","value":10}]""");
        ApplyFailure("""{"Count":1}""", """[{"op":"replace","path":"/COUNT","value":10}]""")
            .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MissingTarget);

        var result = global::Configlue.JsonPatchEngine.Apply(
            Node("""{"Count":1}"""),
            false,
            patch,
            StringComparison.OrdinalIgnoreCase
        );
        result.Node!.ToJsonString().ShouldBe("""{"Count":10}""");
    }

    [Test]
    public void DiffEqualDocumentsAreEmpty()
    {
        global::Configlue.JsonPatchEngine
            .Diff(Node("""{"a":1}"""), false, Node("""{"a":1}"""), false)
            .IsEmpty.ShouldBeTrue();
        // Numerically equal forms diff to nothing.
        global::Configlue.JsonPatchEngine
            .Diff(Node("""{"n":1}"""), false, Node("""{"n":1.0}"""), false)
            .IsEmpty.ShouldBeTrue();
        // Member order never produces operations.
        global::Configlue.JsonPatchEngine
            .Diff(Node("""{"a":1,"b":2}"""), false, Node("""{"b":2,"a":1}"""), false)
            .IsEmpty.ShouldBeTrue();
    }

    [Test]
    public void DiffObjectsRecursively()
    {
        var document = global::Configlue.JsonPatchEngine.Diff(
            Node("""{"a":1,"gone":true,"nested":{"x":1,"y":2}}"""),
            false,
            Node("""{"a":2,"nested":{"x":1,"y":3},"fresh":"n"}"""),
            false
        );
        var result = global::Configlue.JsonPatchEngine.Apply(
            Node("""{"a":1,"gone":true,"nested":{"x":1,"y":2}}"""),
            false,
            document
        );
        result.Node!.ToJsonString()
            .ShouldBe("""{"a":2,"nested":{"x":1,"y":3},"fresh":"n"}""");
    }

    [Test]
    public void DiffEscapesMemberNames()
    {
        var document = global::Configlue.JsonPatchEngine.Diff(
            Node("{}"),
            false,
            Node("""{"a/b":1,"m~n":2}"""),
            false
        );
        var paths = new List<string>();
        foreach (var operation in document.Operations)
        {
            paths.Add(operation.Path);
        }
        paths.ShouldBe(new[] { "/a~1b", "/m~0n" });

        var result = global::Configlue.JsonPatchEngine.Apply(Node("{}"), false, document);
        result.Node!.ToJsonString().ShouldBe("""{"a/b":1,"m~n":2}""");
    }

    [Test]
    public void DiffArraysCollapseToReplace()
    {
        var document = global::Configlue.JsonPatchEngine.Diff(
            Node("""[1,2]"""),
            false,
            Node("""[1,3]"""),
            false
        );
        document.Operations.Count.ShouldBe(1);
        document.Operations[0].Op.ShouldBe("replace");
        var result = global::Configlue.JsonPatchEngine.Apply(Node("""[1,2]"""), false, document);
        result.Node!.ToJsonString().ShouldBe("""[1,3]""");
    }

    [Test]
    public void DiffAbsentHandling()
    {
        global::Configlue.JsonPatchEngine
            .Diff(null, true, null, true)
            .IsEmpty.ShouldBeTrue();

        var added = global::Configlue.JsonPatchEngine.Diff(null, true, Node("""{"a":1}"""), false);
        added.Operations.Count.ShouldBe(1);
        added.Operations[0].Op.ShouldBe("add");

        var removed = global::Configlue.JsonPatchEngine.Diff(
            Node("""{"a":1}"""),
            false,
            null,
            true
        );
        removed.Operations.Count.ShouldBe(1);
        removed.Operations[0].Op.ShouldBe("remove");
    }

    [Test]
    public void SerializeEmptyDocumentIsIndependent()
    {
        var empty = global::Configlue.JsonPatchDocument.Parse("[]");
        var first = global::Configlue.JsonPatchEngine.Serialize(empty);
        var second = global::Configlue.JsonPatchEngine.Serialize(empty);
        Text(first).ShouldBe("[]");
        first.ShouldNotBeSameAs(second);
    }

    [Test]
    public void SerializeRoundTripPreservesExplicitNull()
    {
        var document = Doc("""[{"op":"replace","path":"/a","value":null}]""");
        document.Operations[0].HasValue.ShouldBeTrue();
        var reparsed = global::Configlue.JsonPatchDocument.Parse(Text(
            global::Configlue.JsonPatchEngine.Serialize(document)
        ));
        reparsed.Operations[0].HasValue.ShouldBeTrue();
        reparsed.Operations[0].Value.ShouldBeNull();
        AppliedJson("""{"a":1}""", Text(
            global::Configlue.JsonPatchEngine.Serialize(document)
        )).ShouldBe("""{"a":null}""");
    }
}
