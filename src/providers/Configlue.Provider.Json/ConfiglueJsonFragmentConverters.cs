using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Configlue.Provider.Json;

/// <summary>Non-generic lookup for generated JSON fragment converters.</summary>
/// <remarks>
/// Generated model code registers its fragment converter through
/// <see cref="ConfiglueJsonFragmentRegistry{TFragment}"/>, which mirrors the
/// registration here so trimming and NativeAOT hosts can serialize a fragment
/// from a runtime type without reflection-based serialization. The captured
/// writer delegate closes over the generic converter, so every call site stays
/// statically typed and trim-safe.
/// </remarks>
internal static class ConfiglueJsonFragmentConverters
{
    internal delegate object FragmentReaderDelegate(
        ref Utf8JsonReader reader,
        JsonSerializerOptions options
    );

    private static readonly ConcurrentDictionary<
        Type,
        Action<Utf8JsonWriter, object, JsonSerializerOptions>
    > Writers = new();

    private static readonly ConcurrentDictionary<Type, FragmentReaderDelegate> Readers = new();

    /// <summary>Registers the generated converter for one fragment type.</summary>
    internal static void Register<TFragment>(JsonConverter<TFragment> converter)
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(converter);
        Action<Utf8JsonWriter, object, JsonSerializerOptions> writer = (w, value, options) =>
            converter.Write(w, (TFragment)value, options);
        FragmentReaderDelegate reader = (ref Utf8JsonReader r, JsonSerializerOptions options) =>
            converter.Read(ref r, typeof(TFragment), options)!;
        if (!Writers.TryAdd(typeof(TFragment), writer))
        {
            throw new InvalidOperationException(
                $"Generated JSON converter for fragment '{typeof(TFragment)}' is already registered."
            );
        }

        if (!Readers.TryAdd(typeof(TFragment), reader))
        {
            Writers.TryRemove(typeof(TFragment), out _);
            throw new InvalidOperationException(
                $"Generated JSON converter for fragment '{typeof(TFragment)}' is already registered."
            );
        }
    }

    /// <summary>
    /// Gets the registered fragment writer for a fragment type, initializing generated model
    /// registration on first use.
    /// </summary>
    /// <returns>The generated fragment writer, or <see langword="null"/> when the type is not a generated fragment.</returns>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2059",
        Justification = "The handle comes from the declaring model of a generated fragment type already rooted by this call. Running its static constructor only triggers generated registration."
    )]
    internal static Action<Utf8JsonWriter, object, JsonSerializerOptions>? GetWriterOrNull(
        Type fragmentType
    )
    {
        ArgumentNullException.ThrowIfNull(fragmentType);
        if (Writers.TryGetValue(fragmentType, out var writer))
        {
            return writer;
        }

        if (
            typeof(IConfiglueFragment).IsAssignableFrom(fragmentType)
            && fragmentType.DeclaringType is { } declaringType
        )
        {
            // Running the model's class constructor triggers the generated registration without a
            // module initializer, which Unity does not support.
            RuntimeHelpers.RunClassConstructor(declaringType.TypeHandle);
            if (Writers.TryGetValue(fragmentType, out writer))
            {
                return writer;
            }
        }

        return null;
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2059",
        Justification = "The handle comes from the declaring model of a generated fragment type already rooted by this call. Running its static constructor only triggers generated registration."
    )]
    internal static FragmentReaderDelegate? GetReaderOrNull(Type fragmentType)
    {
        ArgumentNullException.ThrowIfNull(fragmentType);
        if (Readers.TryGetValue(fragmentType, out var reader))
        {
            return reader;
        }

        if (
            typeof(IConfiglueFragment).IsAssignableFrom(fragmentType)
            && fragmentType.DeclaringType is { } declaringType
        )
        {
            RuntimeHelpers.RunClassConstructor(declaringType.TypeHandle);
            if (Readers.TryGetValue(fragmentType, out reader))
            {
                return reader;
            }
        }

        return null;
    }
}
