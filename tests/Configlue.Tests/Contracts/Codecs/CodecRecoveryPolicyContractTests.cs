using System.Text;
using System.Text.Json;
using System.Xml;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using SharpYaml;

namespace Configlue.Tests;

public sealed class CodecRecoveryPolicyContractTests
{
    [Test]
    public void JsonCodec_OnlyClassifiesJsonInputErrorsAsRecoverable()
    {
        IStateCodecRecoveryPolicy policy = new JsonStateCodec<string>();

        policy.IsRecoverableReadException(new JsonException("invalid json")).ShouldBeTrue();
        policy
            .IsRecoverableReadException(new InvalidOperationException("invalid codec setup"))
            .ShouldBeFalse();
    }

    [Test]
    public void XmlCodec_RecognizesXmlErrorsAndWrappedXmlErrors()
    {
        IStateCodecRecoveryPolicy policy = new XmlStateCodec<string>();

        policy.IsRecoverableReadException(new XmlException("invalid xml")).ShouldBeTrue();
        policy
            .IsRecoverableReadException(
                new InvalidOperationException("xml deserialization failed", new XmlException())
            )
            .ShouldBeTrue();
        policy
            .IsRecoverableReadException(
                new InvalidOperationException("invalid codec setup", new FormatException())
            )
            .ShouldBeFalse();
    }

    [Test]
    public void YamlCodec_RecognizesYamlAndInvalidTextEncodingErrors()
    {
        IStateCodecRecoveryPolicy policy = new YamlStateCodec<string>();
        var malformed = new System.Buffers.ReadOnlySequence<byte>(
            Encoding.UTF8.GetBytes("value: [unterminated\n")
        );
        var yamlException = Should.Throw<YamlException>(() =>
            new YamlStateCodec<string>().Deserialize(in malformed, default)
        );

        policy.IsRecoverableReadException(yamlException).ShouldBeTrue();
        policy.IsRecoverableReadException(new DecoderFallbackException()).ShouldBeTrue();
        policy
            .IsRecoverableReadException(new InvalidOperationException("invalid codec setup"))
            .ShouldBeFalse();
    }
}
