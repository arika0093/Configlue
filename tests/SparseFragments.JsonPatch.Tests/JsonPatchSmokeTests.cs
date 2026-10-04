using System.Text;
using System.Text.Json;
using SparseFragments;

namespace SparseFragments.JsonPatch.Tests;

public sealed class JsonPatchSmokeTests
{
    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    [Test]
    public async Task StandaloneReplaceRoundTrip()
    {
        var baseline = new PatchWidget.Fragment
        {
            Name = Optional<string?>.Present("a"),
            Count = Optional<int>.Present(1),
        };

        var patch = PatchWidget.Patch.FromJsonPatch(
            Optional<PatchWidget.Fragment?>.Present(baseline),
            Utf8("""[{"op":"replace","path":"/Name","value":"b"}]"""));

        var applied = baseline.Apply(patch);
        await Assert.That(applied.Name.Value).IsEqualTo("b");
        await Assert.That(applied.Count.Value).IsEqualTo(1);

        var exported = patch.ToJsonPatch(
            Optional<PatchWidget.Fragment?>.Present(baseline));
        var text = Encoding.UTF8.GetString(exported.ToArray());
        await Assert.That(text.Contains("/Name")).IsTrue();
    }
}
