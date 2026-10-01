using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace Configlue.Provider.MessagePack;

/// <summary>Resolves generated Configlue fragment formatters before falling back to another resolver.</summary>
/// <remarks>
/// Use this resolver to compose generated fragment support with a source-generated or custom
/// resolver for ordinary scalar and collection types. Generated fragments never require runtime
/// reflection; the fallback resolver handles the opaque value types a model declares.
/// </remarks>
public sealed class ConfiglueMessagePackResolver : IFormatterResolver
{
    private readonly IFormatterResolver _fallback;

    /// <summary>Creates a resolver using the standard resolver for types without a generated formatter.</summary>
    public ConfiglueMessagePackResolver()
        : this(StandardResolver.Instance) { }

    /// <summary>Creates a resolver that delegates other types to the supplied resolver.</summary>
    public ConfiglueMessagePackResolver(IFormatterResolver? fallback)
    {
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    /// <summary>A shared resolver over the standard reflection-based resolver.</summary>
    public static ConfiglueMessagePackResolver Instance { get; } = new();

    /// <inheritdoc />
    public IMessagePackFormatter<T>? GetFormatter<T>() =>
        ConfiglueMessagePackFragmentRegistry.GetOrNull<T>() ?? _fallback.GetFormatter<T>();
}
