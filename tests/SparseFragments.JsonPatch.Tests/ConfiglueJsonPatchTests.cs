using System.Text.Json;
using Configlue;
using static SparseFragments.JsonPatch.Tests.PatchTestHelpers;

namespace SparseFragments.JsonPatch.Tests;

/// <summary>Configlue-generated model RFC 6902 interop behavior (parity with standalone).</summary>
public sealed class ConfiglueJsonPatchTests
{
    private static global::Configlue.Optional<ConfigluePatchWidget.Fragment?> Present(ConfigluePatchWidget.Fragment fragment) =>
        global::Configlue.Optional<ConfigluePatchWidget.Fragment?>.Present(fragment);

    private static ConfigluePatchWidget.Fragment Baseline() =>
        new()
        {
            Name = global::Configlue.Optional<string?>.Present("alpha"),
            Count = global::Configlue.Optional<int>.Present(1),
            Enabled = global::Configlue.Optional<bool>.Present(true),
            Nested = global::Configlue.Optional<ConfigluePatchNested.Fragment?>.Present(
                new ConfigluePatchNested.Fragment
                {
                    Host = global::Configlue.Optional<string>.Present("example"),
                    Port = global::Configlue.Optional<int>.Present(80),
                }
            ),
            Tags = global::Configlue.Optional<List<string>>.Present(new List<string> { "a", "b" }),
        };

    private static string Canonical(ConfigluePatchWidget.Fragment fragment, JsonSerializerOptions? options = null)
    {
        var effective =
            options is null ? new JsonSerializerOptions() : new JsonSerializerOptions(options);
        effective.Converters.Add(new ConfigluePatchWidget.Fragment.FragmentJsonConverter());
        return JsonSerializer.Serialize(fragment, effective);
    }

    [Test]
    public void AddObjectMember()
    {
        var baseline = new ConfigluePatchWidget.Fragment { Count = global::Configlue.Optional<int>.Present(1) };
        var patch = ConfigluePatchWidget.Patch.FromJsonPatch(
            Present(baseline),
            Utf8("""[{"op":"add","path":"/Name","value":"new"}]"""));
        var applied = baseline.Apply(patch);
        applied.Name.IsPresent.ShouldBeTrue();
        applied.Name.Value.ShouldBe("new");
        applied.Count.Value.ShouldBe(1);
    }

    [Test]
    public void ReplaceExistingMember()
    {
        var baseline = Baseline();
        var patch = ConfigluePatchWidget.Patch.FromJsonPatch(
            Present(baseline),
            Utf8("""[{"op":"replace","path":"/Count","value":42}]"""));
        var applied = baseline.Apply(patch);
        applied.Count.Value.ShouldBe(42);
        applied.Name.Value.ShouldBe("alpha");
    }

    [Test]
    public void ExplicitNullStaysPresent()
    {
        var baseline = Baseline();
        var patch = ConfigluePatchWidget.Patch.FromJsonPatch(
            Present(baseline),
            Utf8("""[{"op":"replace","path":"/Name","value":null}]"""));
        var applied = baseline.Apply(patch);
        applied.Name.IsPresent.ShouldBeTrue();
        applied.Name.Value.ShouldBeNull();
        Canonical(applied).ShouldContain("\"Name\":null");
    }

    [Test]
    public void RemoveBecomesAbsent()
    {
        var baseline = Baseline();
        var patch = ConfigluePatchWidget.Patch.FromJsonPatch(
            Present(baseline),
            Utf8("""[{"op":"remove","path":"/Name"}]"""));
        var applied = baseline.Apply(patch);
        applied.Name.IsPresent.ShouldBeFalse();
        Canonical(applied).ShouldNotContain("Name");
    }

    [Test]
    public void NestedReplace()
    {
        var baseline = Baseline();
        var patch = ConfigluePatchWidget.Patch.FromJsonPatch(
            Present(baseline),
            Utf8("""[{"op":"replace","path":"/Nested/Host","value":"other"}]"""));
        var applied = baseline.Apply(patch);
        applied.Nested.Value!.Host.Value.ShouldBe("other");
        applied.Nested.Value!.Port.Value.ShouldBe(80);
    }

    [Test]
    public void NestedRemoveThenAdd()
    {
        var baseline = Baseline();
        var removed = ConfigluePatchWidget.Patch.FromJsonPatch(
            Present(baseline),
            Utf8("""[{"op":"remove","path":"/Nested/Port"}]"""));
        var withoutPort = baseline.Apply(removed);
        withoutPort.Nested.Value!.Port.IsPresent.ShouldBeFalse();

        var readded = ConfigluePatchWidget.Patch.FromJsonPatch(
            Present(withoutPort),
            Utf8("""[{"op":"add","path":"/Nested/Port","value":8080}]"""));
        var withPort = withoutPort.Apply(readded);
        withPort.Nested.Value!.Port.Value.ShouldBe(8080);
    }

    [Test]
    public void NestedAddFailsWhenParentAbsent()
    {
        var baseline = new ConfigluePatchWidget.Fragment { Count = global::Configlue.Optional<int>.Present(1) };
        var exception = Should.Throw<JsonPatchException>(() =>
            ConfigluePatchWidget.Patch.FromJsonPatch(
                Present(baseline),
                Utf8("""[{"op":"add","path":"/Nested/Host","value":"x"}]""")));
        exception.Kind.ShouldBe(JsonPatchErrorKind.MissingParent);
    }

    [Test]
    public void MoveCollapsesToRemoveAndAdd()
    {
        var baseline = Baseline();
        var patch = ConfigluePatchWidget.Patch.FromJsonPatch(
            Present(baseline),
            Utf8("""[{"op":"move","from":"/Name","path":"/Nested/Host"}]"""));
        var applied = baseline.Apply(patch);
        applied.Name.IsPresent.ShouldBeFalse();
        applied.Nested.Value!.Host.Value.ShouldBe("alpha");

        var exported = Text(patch.ToJsonPatch(Present(baseline)));
        exported.ShouldNotContain("\"move\"");
        exported.ShouldContain("\"remove\"");
        // Collapsed to remove plus add or replace depending on baseline presence.
        (exported.Contains("\"add\"") || exported.Contains("\"replace\"")).ShouldBeTrue();
    }

    [Test]
    public void CopyCollapsesToAdd()
    {
        var baseline = Baseline();
        var patch = ConfigluePatchWidget.Patch.FromJsonPatch(
            Present(baseline),
            Utf8("""[{"op":"copy","from":"/Name","path":"/Nested/Host"}]"""));
        var applied = baseline.Apply(patch);
        applied.Name.Value.ShouldBe("alpha");
        applied.Nested.Value!.Host.Value.ShouldBe("alpha");

        var exported = Text(patch.ToJsonPatch(Present(baseline)));
        exported.ShouldNotContain("\"copy\"");
    }

    [Test]
    public void TestSuccessFailureAndDroppedOnExport()
    {
        var baseline = Baseline();
        var ok = ConfigluePatchWidget.Patch.FromJsonPatch(
            Present(baseline),
            Utf8(
                """[{"op":"test","path":"/Count","value":1},{"op":"replace","path":"/Count","value":2}]"""));
        baseline.Apply(ok).Count.Value.ShouldBe(2);

        var exception = Should.Throw<JsonPatchException>(() =>
            ConfigluePatchWidget.Patch.FromJsonPatch(
                Present(baseline),
                Utf8("""[{"op":"test","path":"/Count","value":99}]""")));
        exception.Kind.ShouldBe(JsonPatchErrorKind.TestFailed);

        Text(ok.ToJsonPatch(Present(baseline))).ShouldNotContain("\"test\"");
    }

    [Test]
    public void RootReplaceObjectNullAndRemove()
    {
        var baseline = Baseline();

        var replaced = ConfigluePatchWidget.Patch.FromJsonPatch(
            Present(baseline),
            Utf8("""[{"op":"replace","path":"","value":{"Count":7}}]"""));
        var applied = baseline.Apply(replaced);
        applied.Count.Value.ShouldBe(7);
        applied.Name.IsPresent.ShouldBeFalse();

        var nulled = ConfigluePatchWidget.Patch.FromJsonPatch(
            Present(baseline),
            Utf8("""[{"op":"replace","path":"","value":null}]"""));
        var nullResult = nulled.ApplyNested(Present(baseline));
        nullResult.IsPresent.ShouldBeTrue();
        nullResult.Value.ShouldBeNull();

        var removed = ConfigluePatchWidget.Patch.FromJsonPatch(
            Present(baseline),
            Utf8("""[{"op":"remove","path":""}]"""));
        var absent = removed.ApplyNested(Present(baseline));
        absent.IsPresent.ShouldBeFalse();
    }

    [Test]
    public void RootAddFromAbsent()
    {
        var patch = ConfigluePatchWidget.Patch.FromJsonPatch(
            global::Configlue.Optional<ConfigluePatchWidget.Fragment?>.Missing,
            Utf8("""[{"op":"add","path":"","value":{"Count":3}}]"""));
        var result = patch.ApplyNested(global::Configlue.Optional<ConfigluePatchWidget.Fragment?>.Missing);
        result.IsPresent.ShouldBeTrue();
        result.Value!.Count.Value.ShouldBe(3);
    }

    [Test]
    public void ArrayInsertRemoveAppend()
    {
        var baseline = Baseline();
        var patch = ConfigluePatchWidget.Patch.FromJsonPatch(
            Present(baseline),
            Utf8(
                """[{"op":"add","path":"/Tags/1","value":"x"},{"op":"remove","path":"/Tags/0"},{"op":"add","path":"/Tags/-","value":"z"}]"""));
        var applied = baseline.Apply(patch);
        applied.Tags.Value.ShouldBe(new[] { "x", "b", "z" });
    }

    [Test]
    public void CollectionExportIsWholeReplace()
    {
        var baseline = Baseline();
        var patch = ConfigluePatchWidget.Patch.FromJsonPatch(
            Present(baseline),
            Utf8("""[{"op":"add","path":"/Tags/-","value":"c"}]"""));
        var exported = Text(patch.ToJsonPatch(Present(baseline)));
        exported.ShouldContain("/Tags");
        exported.ShouldNotContain("/Tags/2");
        exported.ShouldNotContain("/Tags/-");
    }

    [Test]
    public void PointerEscaping()
    {
        var baseline = new ConfigluePatchNaming.Fragment
        {
            Value = global::Configlue.Optional<string?>.Present("v"),
            Slash = global::Configlue.Optional<int>.Present(1),
            Tilde = global::Configlue.Optional<int>.Present(2),
            Plain = global::Configlue.Optional<int>.Present(3),
        };
        var patch = ConfigluePatchNaming.Patch.FromJsonPatch(
            global::Configlue.Optional<ConfigluePatchNaming.Fragment?>.Present(baseline),
            Utf8(
                """[{"op":"replace","path":"/a~1b","value":10},{"op":"replace","path":"/m~0n","value":20}]"""));
        var applied = baseline.Apply(patch);
        applied.Slash.Value.ShouldBe(10);
        applied.Tilde.Value.ShouldBe(20);
    }

    [Test]
    public void JsonPropertyNameHonored()
    {
        var baseline = new ConfigluePatchNaming.Fragment { Value = global::Configlue.Optional<string?>.Present("v") };
        var patch = ConfigluePatchNaming.Patch.FromJsonPatch(
            global::Configlue.Optional<ConfigluePatchNaming.Fragment?>.Present(baseline),
            Utf8("""[{"op":"replace","path":"/customName","value":"w"}]"""));
        baseline.Apply(patch).Value.Value.ShouldBe("w");

        // CLR names are never the wire path when JSON naming differs: replace fails
        // at the canonical document (missing target), add reaches fragment mapping.
        var replaceException = Should.Throw<JsonPatchException>(() =>
            ConfigluePatchNaming.Patch.FromJsonPatch(
                global::Configlue.Optional<ConfigluePatchNaming.Fragment?>.Present(baseline),
                Utf8("""[{"op":"replace","path":"/Value","value":"w"}]""")));
        replaceException.Kind.ShouldBe(JsonPatchErrorKind.MissingTarget);

        var addException = Should.Throw<JsonPatchException>(() =>
            ConfigluePatchNaming.Patch.FromJsonPatch(
                global::Configlue.Optional<ConfigluePatchNaming.Fragment?>.Present(baseline),
                Utf8("""[{"op":"add","path":"/Value","value":"w"}]""")));
        addException.Kind.ShouldBe(JsonPatchErrorKind.UnmappedProperty);
    }

    [Test]
    public void NamingPolicyAndCaseSensitivity()
    {
        var baseline = Baseline();
        var camel = PatchTestHelpers.CamelCase();

        var patch = ConfigluePatchWidget.Patch.FromJsonPatch(
            Present(baseline),
            Utf8("""[{"op":"replace","path":"/count","value":9}]"""),
            camel);
        baseline.Apply(patch).Count.Value.ShouldBe(9);

        var wrongCase = Should.Throw<JsonPatchException>(() =>
            ConfigluePatchWidget.Patch.FromJsonPatch(
                Present(baseline),
                Utf8("""[{"op":"replace","path":"/COUNT","value":9}]""")));
        wrongCase.Kind.ShouldBe(JsonPatchErrorKind.MissingTarget);

        var insensitive = PatchTestHelpers.CaseInsensitive();
        var patch2 = ConfigluePatchWidget.Patch.FromJsonPatch(
            Present(baseline),
            Utf8("""[{"op":"replace","path":"/COUNT","value":10}]"""),
            insensitive);
        baseline.Apply(patch2).Count.Value.ShouldBe(10);
    }

    [Test]
    public void ImportExportSemanticRoundTrip()
    {
        var baseline = Baseline();
        var jsonPatch = Utf8(
            """[{"op":"replace","path":"/Name","value":"beta"},{"op":"add","path":"/Tags/-","value":"c"},{"op":"remove","path":"/Nested/Port"}]""");

        var patch = ConfigluePatchWidget.Patch.FromJsonPatch(Present(baseline), jsonPatch);
        var viaPatch = baseline.Apply(patch);

        var reparsed = ConfigluePatchWidget.Patch.FromJsonPatch(Present(baseline), jsonPatch);
        Canonical(baseline.Apply(reparsed)).ShouldBe(Canonical(viaPatch));

        var exported = patch.ToJsonPatch(Present(baseline));
        var reimported = ConfigluePatchWidget.Patch.FromJsonPatch(
            Present(baseline),
            exported.ToArray());
        Canonical(baseline.Apply(reimported)).ShouldBe(Canonical(viaPatch));
    }

    [Test]
    public void WholeContributionTransitions()
    {
        var toPresent = ConfigluePatchWidget.Patch.FromJsonPatch(
            global::Configlue.Optional<ConfigluePatchWidget.Fragment?>.Missing,
            Utf8("""[{"op":"add","path":"","value":{"Count":1}}]"""));
        var present = toPresent.ApplyNested(global::Configlue.Optional<ConfigluePatchWidget.Fragment?>.Missing);
        present.IsPresent.ShouldBeTrue();

        var toNull = ConfigluePatchWidget.Patch.FromJsonPatch(
            present,
            Utf8("""[{"op":"replace","path":"","value":null}]"""));
        var nulled = toNull.ApplyNested(present);
        nulled.IsPresent.ShouldBeTrue();
        nulled.Value.ShouldBeNull();

        var backToObject = ConfigluePatchWidget.Patch.FromJsonPatch(
            nulled,
            Utf8("""[{"op":"replace","path":"","value":{"Count":2}}]"""));
        var obj = backToObject.ApplyNested(nulled);
        obj.Value!.Count.Value.ShouldBe(2);

        var baseline = Baseline();
        var toAbsent = ConfigluePatchWidget.Patch.FromJsonPatch(
            Present(baseline),
            Utf8("""[{"op":"remove","path":""}]"""));
        var absent = toAbsent.ApplyNested(Present(baseline));
        absent.IsPresent.ShouldBeFalse();

        var absentToNull = ConfigluePatchWidget.Patch.FromJsonPatch(
            absent,
            Utf8("""[{"op":"add","path":"","value":null}]"""));
        var presentNull = absentToNull.ApplyNested(absent);
        presentNull.IsPresent.ShouldBeTrue();
        presentNull.Value.ShouldBeNull();
    }

    [Test]
    public void OperationsApplyInOrderAtomically()
    {
        var baseline = Baseline();
        var exception = Should.Throw<JsonPatchException>(() =>
            ConfigluePatchWidget.Patch.FromJsonPatch(
                Present(baseline),
                Utf8(
                    """[{"op":"replace","path":"/Count","value":5},{"op":"remove","path":"/Missing"}]""")));
        exception.Kind.ShouldBe(JsonPatchErrorKind.MissingTarget);
    }

    [Test]
    public void RoutingSurvivesJsonPatchBridge()
    {
        // JSON Patch interop is an adapter: routed patch capabilities still work.
        var baseline = Baseline();
        var patch = ConfigluePatchWidget.Patch.FromJsonPatch(
            Present(baseline),
            Utf8("""[{"op":"replace","path":"/Count","value":11}]"""));
        patch.IsEmpty.ShouldBeFalse();
        var routed = (Configlue.CompilerServices.IConfiglueRoutablePatch)patch;
        routed.ShouldNotBeNull();
        var withUnset = patch.WithUnspecifiedMembersUnset();
        withUnset.ShouldNotBeNull();
    }
}
