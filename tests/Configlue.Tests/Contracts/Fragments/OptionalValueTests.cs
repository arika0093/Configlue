namespace Configlue.Tests;

public sealed class OptionalValueTests
{
    [Test]
    public void EqualityDistinguishesMissingFromPresentDefaultAndKeepsHashCodesConsistent()
    {
        var missing = Optional<int>.Missing;
        var presentDefault = Optional<int>.Present(0);
        var presentNull = Optional<string?>.Present(null);
        var missingString = Optional<string?>.Missing;
        var first = Optional<string>.Present("value");
        var second = Optional<string>.Present(new string("value".ToCharArray()));

        missing.ShouldNotBe(presentDefault);
        presentNull.ShouldNotBe(missingString);
        first.ShouldBe(second);
        first.GetHashCode().ShouldBe(second.GetHashCode());
    }
}
