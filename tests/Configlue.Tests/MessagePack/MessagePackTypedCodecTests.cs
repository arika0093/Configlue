using Configlue.Provider.MessagePack;
using MessagePack;

namespace Configlue.Tests;

public sealed class MessagePackTypedCodecTests
{
    [Test]
    public void TypedCodec_RequiresGeneratedFragmentFormatter()
    {
        var failure = Should.Throw<InvalidOperationException>(() =>
            new MessagePackStateCodec<int>(MessagePackSerializerOptions.Standard)
        );

        failure.Message.ShouldContain("registered generated Configlue fragment formatter");
        failure.Message.ShouldContain("non-generic MessagePackStateCodec");
    }
}
