using System.Text.Json.Serialization;

namespace Configlue.Provider.Json;

/// <summary>Provides generated JSON converters to provider code without coupling Abstraction to JSON.</summary>
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
