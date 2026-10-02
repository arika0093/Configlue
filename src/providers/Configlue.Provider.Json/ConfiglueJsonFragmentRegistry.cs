using System.Text.Json.Serialization;

namespace Configlue.Provider.Json;

/// <summary>Provides generated fragment-shape converters to JSON provider code.</summary>
/// <remarks>
/// The converter handles the Configlue fragment envelope and nested generated fragments. Scalar and collection
/// members still use System.Text.Json metadata from the supplied <see cref="System.Text.Json.JsonSerializerOptions.TypeInfoResolver"/>.
/// NativeAOT applications must supply a source-generated resolver that covers those member types.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class ConfiglueJsonFragmentRegistry<TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private static JsonConverter<TFragment>? _converter;

    /// <summary>Gets the generated JSON converter registered for this fragment.</summary>
    public static JsonConverter<TFragment> Converter =>
        _converter
        ?? throw new InvalidOperationException(
            $"Generated JSON converter for fragment '{typeof(TFragment)}' has not been registered."
        );

    /// <summary>Tries to get the generated JSON converter registered for this fragment.</summary>
    public static bool TryGetConverter(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out JsonConverter<TFragment>? converter
    )
    {
        converter = _converter;
        return converter is not null;
    }

    /// <summary>Registers the generated JSON converter for this fragment.</summary>
    public static void Register(JsonConverter<TFragment> converter)
    {
        ArgumentNullException.ThrowIfNull(converter);
        if (Interlocked.CompareExchange(ref _converter, converter, null) is not null)
        {
            throw new InvalidOperationException(
                $"Generated JSON converter for fragment '{typeof(TFragment)}' is already registered."
            );
        }
    }
}
