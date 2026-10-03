using Configlue.Extensibility;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class TransformerCompositionContractTests
{
    [Test]
    public void TransformingResource_RejectsMarkerOnlyAndNullTransformersImmediately()
    {
        var resource = new InMemoryResource();
        Should.Throw<ArgumentException>(() => new TransformingResource(resource, [new MarkerOnly()]));
        Should.Throw<ArgumentException>(() => new TransformingResource(resource, [null!]));
    }

    private sealed class MarkerOnly : IStateByteTransformer { }
}
