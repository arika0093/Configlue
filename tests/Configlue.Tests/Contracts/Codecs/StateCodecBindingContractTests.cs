using System.Buffers;
using Configlue.Codecs;

namespace Configlue.Tests;

public sealed class StateCodecBindingContractTests
{
    [Test]
    public void DualContractCodecRequiresAndHonorsExplicitTypedOrDynamicBinding()
    {
        var codec = new DualContractCodec();
        var typed = StateCodecBinding.Typed<string>(codec);
        var dynamic = StateCodecBinding.Dynamic(codec);

        typed.StateType.ShouldBe(typeof(string));
        typed.TryGetTyped<string>(out var typedCodec).ShouldBeTrue();
        typedCodec.ShouldBeSameAs(codec);
        typed.DynamicCodec.ShouldBeNull();

        dynamic.StateType.ShouldBeNull();
        dynamic.TryGetTyped<string>(out _).ShouldBeFalse();
        dynamic.DynamicCodec.ShouldBeSameAs(codec);
    }

    private sealed class DualContractCodec : IStateCodec<string>, IStateCodec
    {
        public string? Deserialize(
            in ReadOnlySequence<byte> source,
            in StateCodecContext context
        ) => "typed";

        public void Serialize(
            string? value,
            IBufferWriter<byte> destination,
            in StateCodecContext context
        ) { }

        object? IStateCodec.Deserialize(
            Type type,
            in ReadOnlySequence<byte> source,
            in StateCodecContext context
        ) => "dynamic";

        void IStateCodec.Serialize(
            Type type,
            object? value,
            IBufferWriter<byte> destination,
            in StateCodecContext context
        ) { }
    }
}
