using MessagePack;
using MessagePack.Formatters;

namespace Configlue.Provider.MessagePack;

/// <summary>
/// An int-backed MessagePack formatter for enum member types on trimming and NativeAOT hosts.
/// </summary>
/// <typeparam name="TEnum">The enum type. Values round-trip as 32-bit integers.</typeparam>
/// <remarks>
/// MessagePack 3.x generated resolvers cover <c>[MessagePackObject]</c> types but not plain
/// enums, and the dynamic enum resolvers are not trimming- or NativeAOT-clean. Register one
/// instance per enum type in the closed-world leaf resolver (see
/// <see cref="ConfiglueMessagePackResolver.CreateClosedWorldOptions"/>). Undefined numeric
/// values deserialize without validation, matching MessagePack enum semantics.
/// </remarks>
public sealed class MessagePackEnumFormatter<TEnum> : IMessagePackFormatter<TEnum>
    where TEnum : struct, Enum
{
    /// <summary>A shared formatter instance.</summary>
    public static MessagePackEnumFormatter<TEnum> Instance { get; } = new();

    /// <inheritdoc />
    public void Serialize(
        ref MessagePackWriter writer,
        TEnum value,
        MessagePackSerializerOptions options
    ) => writer.Write(Convert.ToInt32(value));

    /// <inheritdoc />
    public TEnum Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) =>
        (TEnum)Enum.ToObject(typeof(TEnum), reader.ReadInt32());
}
