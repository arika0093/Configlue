using System.Text;
using SparseFragments;

namespace SparseFragments.Tests;

/// <summary>
/// Package-level proof for #330: installing only <c>SparseFragments</c> is sufficient
/// for <c>FromJsonPatch</c> / <c>ToJsonPatch</c>. This project references only
/// <c>SparseFragments</c> (plus its generator analyzer), so any use of the bridge here
/// fails to compile if the split-package activation returns.
/// </summary>
[SparseFragmentModel]
public partial class PackageLevelWidget
{
    public string? Name { get; set; }

    public int Count { get; set; }
}

public sealed class JsonPatchPackageLevelTests
{
    [Test]
    public async Task FromAndToJsonPatch_WorkWithoutExtraPackage()
    {
        var baseline = new PackageLevelWidget.Fragment
        {
            Name = Optional<string?>.Present("a"),
            Count = Optional<int>.Present(1),
        };

        var patch = PackageLevelWidget.Patch.FromJsonPatch(
            Optional<PackageLevelWidget.Fragment?>.Present(baseline),
            Encoding.UTF8.GetBytes("""[{"op":"replace","path":"/Name","value":"b"}]""")
        );

        var applied = baseline.Apply(patch);
        await Assert.That(applied.Name.Value).IsEqualTo("b");
        await Assert.That(applied.Count.Value).IsEqualTo(1);

        var exported = patch.ToJsonPatch(Optional<PackageLevelWidget.Fragment?>.Present(baseline));
        var text = Encoding.UTF8.GetString(exported.ToArray());
        await Assert.That(text.Contains("/Name")).IsTrue();

        // Runtime types live natively in SparseFragments (no .JsonPatch sub-namespace).
        var document = SparseJsonPatch.Parse("""[{"op":"replace","path":"/Count","value":2}]""");
        await Assert.That(document.IsEmpty).IsFalse();
    }
}
