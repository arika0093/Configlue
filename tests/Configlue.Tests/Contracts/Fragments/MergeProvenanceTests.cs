namespace Configlue.Tests;

[ConfiglueModel("merge-provenance-review")]
public partial class ProvenanceReviewSettings
{
    [ConfiglueMerge(MergeMode.SetUnion)]
    public ISet<string> Set { get; set; } = new HashSet<string>();

    [ConfiglueMerge(MergeMode.Append)]
    public IReadOnlyList<string> Appended { get; set; } = [];

    public List<string> Replaced { get; set; } = [];
}

public sealed class MergeProvenanceTests
{
    [Test]
    public void SetUnionExplanationUsesSetComparersAndMapsPriorityIndices()
    {
        var member = ProvenanceReviewSettings.ConfiglueSchema.Members.Single(member =>
            member.Name == "Set"
        );
        var lower = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "alpha" };
        var higher = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ALPHA" };
        var elements = ConfiglueMergeProvenance.ExplainElements(
            member,
            lower,
            [("higher", higher), ("lower", lower)]
        );
        elements.Single().SourceIndices.ShouldBe([0, 1]);
    }

    [Test]
    public void AppendExplanationHonorsNullReset()
    {
        var member = ProvenanceReviewSettings.ConfiglueSchema.Members.Single(member =>
            member.Name == "Appended"
        );
        var elements = ConfiglueMergeProvenance.ExplainElements(
            member,
            new[] { "same" },
            [("higher", new[] { "same" }), ("reset", null), ("lower", new[] { "same" })]
        );
        elements.Single().SourceIndices.ShouldBe([0]);
    }

    [Test]
    public void ReplaceExplanationUsesOnlyTheWinningSource()
    {
        var member = ProvenanceReviewSettings.ConfiglueSchema.Members.Single(member =>
            member.Name == "Replaced"
        );
        var elements = ConfiglueMergeProvenance.ExplainElements(
            member,
            new[] { "same" },
            [("higher", new[] { "same" }), ("lower", new[] { "same" })]
        );
        elements.Single().SourceIndices.ShouldBe([0]);
    }
}
