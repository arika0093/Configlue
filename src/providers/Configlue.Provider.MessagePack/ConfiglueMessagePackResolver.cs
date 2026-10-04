using System.Diagnostics.CodeAnalysis;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace Configlue.Provider.MessagePack;

/// <summary>Resolves generated Configlue fragment formatters before delegating leaf types to another resolver.</summary>
/// <remarks>
/// This resolver handles only generated fragment envelopes. Scalar and collection members are
/// delegated to the fallback resolver. The parameterless constructor and <see cref="Instance"/>
/// use <see cref="StandardResolver.Instance"/>, which can generate formatters through runtime
/// reflection and is not an AOT guarantee. For NativeAOT, use
/// <see cref="ConfiglueMessagePackResolver(IFormatterResolver)"/> or
/// <see cref="CreateClosedWorldOptions(IFormatterResolver[])"/> with an AOT-safe fallback
/// that covers every leaf member type, such as a MessagePack 3.x generated resolver composed
/// with explicit primitive, collection, and enum formatters.
/// </remarks>
public sealed class ConfiglueMessagePackResolver : IFormatterResolver
{
    private readonly IFormatterResolver _fallback;

    /// <summary>Creates a resolver using the standard resolver for types without a generated fragment formatter.</summary>
    /// <remarks>
    /// The standard resolver can generate formatters through runtime reflection and keeps
    /// MessagePack's dynamic fallback reachable for trimming and NativeAOT. NativeAOT hosts
    /// must use <see cref="ConfiglueMessagePackResolver(IFormatterResolver)"/> or
    /// <see cref="CreateClosedWorldOptions(IFormatterResolver[])"/> with an
    /// AOT-safe fallback instead.
    /// </remarks>
    [RequiresDynamicCode(
        "The standard resolver may generate formatters through runtime reflection. Use an explicit AOT-safe fallback resolver for NativeAOT."
    )]
    public ConfiglueMessagePackResolver()
        : this(StandardResolver.Instance) { }

    /// <summary>Creates a resolver that delegates other types to the supplied resolver.</summary>
    public ConfiglueMessagePackResolver(IFormatterResolver? fallback)
    {
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    /// <summary>A shared resolver over the standard resolver, which may use runtime reflection for leaf types.</summary>
    /// <remarks>
    /// Intentionally constructed on each access inside the annotated getter so merely
    /// referencing this type (for example to use the explicit-fallback constructor from
    /// NativeAOT code) never roots the standard resolver's dynamic fallback.
    /// </remarks>
    public static ConfiglueMessagePackResolver Instance
    {
        [RequiresDynamicCode(
            "The standard resolver may generate formatters through runtime reflection. Use an explicit AOT-safe fallback resolver for NativeAOT."
        )]
        get => new(StandardResolver.Instance);
    }

    /// <summary>
    /// Creates trimming- and NativeAOT-clean MessagePack options over generated Configlue
    /// fragment formatters and the supplied leaf resolvers.
    /// </summary>
    /// <param name="leafResolvers">
    /// Resolvers for scalar, enum, and collection member types, tried in order after the
    /// generated fragment registry. Pass the application's MessagePack 3.x generated resolver
    /// together with explicit formatters (concrete primitive formatters such as
    /// <c>Int32Formatter</c>, per-collection <c>ListFormatter&lt;T&gt;</c> instances, and
    /// <c>MessagePackEnumFormatter&lt;TEnum&gt;</c> for enums) so no runtime-reflection
    /// fallback is reachable. This factory never references
    /// <see cref="StandardResolver.Instance"/> or <c>MessagePackSerializerOptions.Standard</c>.
    /// </param>
    /// <remarks>
    /// This is the supported NativeAOT entry point. The parameterless constructors,
    /// <see cref="Instance"/>, and the default codec options keep the broader
    /// reflection-capable behavior for non-AOT hosts and are annotated accordingly.
    /// </remarks>
    public static MessagePackSerializerOptions CreateClosedWorldOptions(
        params IFormatterResolver[] leafResolvers
    )
    {
        ArgumentNullException.ThrowIfNull(leafResolvers);
        IFormatterResolver leaf =
            leafResolvers.Length == 1 ? leafResolvers[0] : CompositeResolver.Create(leafResolvers);
        return new MessagePackSerializerOptions(new ConfiglueMessagePackResolver(leaf));
    }

    /// <inheritdoc />
    public IMessagePackFormatter<T>? GetFormatter<T>() =>
        ConfiglueMessagePackFragmentRegistry.GetOrNull<T>() ?? _fallback.GetFormatter<T>();
}
