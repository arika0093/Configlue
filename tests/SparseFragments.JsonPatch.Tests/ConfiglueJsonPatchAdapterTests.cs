using System.Text.Json.Nodes;
using Configlue;
using static SparseFragments.JsonPatch.Tests.PatchTestHelpers;

namespace SparseFragments.JsonPatch.Tests;

// Thin Configlue integration tests: the public adapter surface
// (global::Configlue.ConfiglueJsonPatch facade, public operation constructors,
// JsonPatchException/Kind propagation). Generated-model parity lives in
// ConfiglueJsonPatchTests; product-neutral RFC mechanics live in
// JsonPatchBehavioralTests.
public sealed class ConfiglueJsonPatchAdapterTests
{
    private static JsonNode Node(string json) => JsonNode.Parse(json)!;

    [Test]
    public void FacadeParseMatchesDocumentParse()
    {
        const string json = """[{"op":"replace","path":"/a","value":1}]""";
        var viaFacade = global::Configlue.ConfiglueJsonPatch.Parse(Utf8(json));
        viaFacade.Operations.Count.ShouldBe(1);
        viaFacade.Operations[0].Op.ShouldBe("replace");

        var viaString = global::Configlue.ConfiglueJsonPatch.Parse(json);
        viaString.Operations.Count.ShouldBe(1);

        var viaDocument = global::Configlue.JsonPatchDocument.Parse(json);
        viaDocument.Operations[0].Path.ShouldBe(viaFacade.Operations[0].Path);
    }

    [Test]
    public void FacadeApplyDiffSerializeDelegateToEngine()
    {
        var baseline = Node("""{"a":1}""");
        var document = global::Configlue.ConfiglueJsonPatch.Parse(
            """[{"op":"replace","path":"/a","value":2}]"""
        );

        var viaFacade = global::Configlue.ConfiglueJsonPatch.Apply(baseline, false, document);
        viaFacade.Node!.ToJsonString().ShouldBe("""{"a":2}""");

        var diff = global::Configlue.ConfiglueJsonPatch.Diff(
            Node("""{"a":1}"""),
            false,
            Node("""{"a":2}"""),
            false
        );
        Text(global::Configlue.ConfiglueJsonPatch.Serialize(diff))
            .ShouldBe(Text(global::Configlue.JsonPatchEngine.Serialize(diff)));
    }

    [Test]
    public void PublicOperationConstructorsExposeOperands()
    {
        // Hand-built operations expose the same operands the parser captures;
        // equivalent parsed documents take effect through the engine.
        var replace = new global::Configlue.JsonPatchOperation(
            "replace",
            "/a",
            null,
            Node("2"),
            true
        );
        replace.Op.ShouldBe("replace");
        replace.Path.ShouldBe("/a");
        replace.From.ShouldBeNull();
        replace.HasValue.ShouldBeTrue();

        var move = new global::Configlue.JsonPatchOperation("move", "/b", "/a", null, false);
        move.From.ShouldBe("/a");
        var result = global::Configlue.JsonPatchEngine.Apply(
            Node("""{"a":1}"""),
            false,
            global::Configlue.ConfiglueJsonPatch.Parse(
                """[{"op":"move","from":"/a","path":"/b"}]"""
            )
        );
        result.Node!.ToJsonString().ShouldBe("""{"b":1}""");

        var copy = new global::Configlue.JsonPatchOperation("copy", "/b", "/a", null, false);
        var copied = global::Configlue.JsonPatchEngine.Apply(
            Node("""{"a":1}"""),
            false,
            global::Configlue.ConfiglueJsonPatch.Parse(
                """[{"op":"copy","from":"/a","path":"/b"}]"""
            )
        );
        copy.From.ShouldBe("/a");
        copied.Node!.ToJsonString().ShouldBe("""{"a":1,"b":1}""");
    }

    [Test]
    public void ErrorKindsSurfaceThroughFacade()
    {
        Should
            .Throw<global::Configlue.JsonPatchException>(() =>
                global::Configlue.ConfiglueJsonPatch.Parse("{bad")
            )
            .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MalformedDocument);

        Should
            .Throw<global::Configlue.JsonPatchException>(() =>
                global::Configlue.ConfiglueJsonPatch.Apply(
                    Node("""{"a":1}"""),
                    false,
                    global::Configlue.ConfiglueJsonPatch.Parse(
                        """[{"op":"remove","path":"/missing"}]"""
                    )
                )
            )
            .Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.MissingTarget);
    }

    [Test]
    public void ExceptionCarriesKindAndMessage()
    {
        var exception = new global::Configlue.JsonPatchException(
            global::Configlue.JsonPatchErrorKind.TestFailed,
            "custom"
        );
        exception.Kind.ShouldBe(global::Configlue.JsonPatchErrorKind.TestFailed);
        exception.Message.ShouldBe("custom");

        var inner = new InvalidOperationException("inner");
        var wrapped = new global::Configlue.JsonPatchException(
            global::Configlue.JsonPatchErrorKind.MissingParent,
            "wrapped",
            inner
        );
        wrapped.InnerException.ShouldBeSameAs(inner);
    }

    [Test]
    public void ExplicitNullStaysPresentThroughPublicSurface()
    {
        var document = global::Configlue.ConfiglueJsonPatch.Parse(
            """[{"op":"replace","path":"/a","value":null}]"""
        );
        document.Operations[0].HasValue.ShouldBeTrue();
        var result = global::Configlue.ConfiglueJsonPatch.Apply(
            Node("""{"a":1}"""),
            false,
            document
        );
        result.Node!.ToJsonString().ShouldBe("""{"a":null}""");
    }
}
