using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace Configlue.Provider.MessagePack;

/// <summary>Resolves generated Configlue fragment formatters before delegating leaf types to another resolver.</summary>
/// <remarks>
/// This resolver handles only generated fragment envelopes. Scalar and collection members are
/// delegated to the fallback resolver. The parameterless constructor and <see cref="Instance"/>
/// use <see cref="StandardResolver.Instance"/>, which can generate formatters through runtime
/// reflection and is not an AOT guarantee. For NativeAOT, pass an AOT-safe fallback that covers
/// every leaf member type, such as a MessagePack 3.x generated resolver composed with explicit
/// collection and built-in formatters.
/// </remarks>
public sealed class ConfiglueMessagePackResolver : IFormatterResolver
{
    private readonly IFormatterResolver _fallback;

    /// <summary>Creates a resolver using the standard resolver for types without a generated fragment formatter.</summary>
    public ConfiglueMessagePackResolver()
        : this(StandardResolver.Instance) { }

    /// <summary>Creates a resolver that delegates other types to the supplied resolver.</summary>
    public ConfiglueMessagePackResolver(IFormatterResolver? fallback)
    {
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    /// <summary>A shared resolver over the standard resolver, which may use runtime reflection for leaf types.</summary>
    public static ConfiglueMessagePackResolver Instance { get; } = new();

    /// <inheritdoc />
    public IMessagePackFormatter<T>? GetFormatter<T>() =>
        ConfiglueMessagePackFragmentRegistry.GetOrNull<T>() ?? _fallback.GetFormatter<T>();
}
