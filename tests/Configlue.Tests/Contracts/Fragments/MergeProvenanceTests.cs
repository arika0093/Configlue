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
    // Per-element provenance is an explicit advanced opt-in (#288) exercised here directly.
    // Default generated Details carry member-level provenance only; Append/SetUnion/custom
    // per-element graphs are not part of the default API, so only the member replace
    // representative is kept.
    [Test]
    public void ReplaceExplanationUsesOnlyTheWinningSource()
    {
        var member = ProvenanceReviewSettings.ConfiglueSchema.Members.Single(member =>
            member.Name == "Replaced"
        );
        var elements = ConfiglueMergeProvenance.ExplainElements(
            member,
            new[] { "same" },
            [(SourceId.From("higher"), new[] { "same" }), (SourceId.From("lower"), new[] { "same" })]
        );
        elements.Single().SourceIndices.ShouldBe([0]);
    }
}
