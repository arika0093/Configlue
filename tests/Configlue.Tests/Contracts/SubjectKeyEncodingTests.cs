using System.Text;

namespace Configlue.Tests;

public sealed class SubjectKeyEncodingTests
{
    [Test]
    [Arguments("a")]
    [Arguments("ab")]
    [Arguments("abc")]
    [Arguments("tenant/a")]
    [Arguments("e\u0301")]
    [Arguments("日本語😀")]
    [Arguments("\u083e\u083f")]
    public void SingleSegmentMatchesCanonicalEncoding(string segment)
    {
        var encoded = Convert
            .ToBase64String(Encoding.UTF8.GetBytes(segment.Normalize()))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var expected = $"subject:{encoded.Length}:{encoded}";
        SubjectKey.From(segment).Value.ShouldBe(expected);
        SubjectKey.FromSegments(segment).Value.ShouldBe(expected);
    }

    [Test]
    public void LargeSingleSegmentMatchesSegmentArray()
    {
        var segment = new string('a', 100_000);
        SubjectKey.From(segment).ShouldBe(SubjectKey.FromSegments(segment));
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("\ud800")]
    public void InvalidSingleSegmentRetainsValidation(string? segment)
    {
        Should.Throw<ArgumentException>(() => SubjectKey.From(segment!));
        Should.Throw<ArgumentException>(() => SubjectKey.FromSegments(segment!));
    }
}
