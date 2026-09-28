using System.Security.Cryptography;
using System.Text.Json;
using Configlue.Transformer.AES;

namespace Configlue.Tests;

public sealed class TransformerRecoveryPolicyContractTests
{
    [Test]
    public void AesTransformer_OnlyClassifiesCryptographicReadErrorsAsRecoverable()
    {
        using var transformer = new AesGcmStateByteTransformer(new byte[32]);
        IStateByteTransformerRecoveryPolicy policy = transformer;

        policy
            .IsRecoverableReadException(new CryptographicException("invalid ciphertext"))
            .ShouldBeTrue();
        policy.IsRecoverableReadException(new JsonException("invalid json")).ShouldBeFalse();
    }
}
