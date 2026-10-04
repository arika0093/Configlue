using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Configlue.Source.Consul;

/// <summary>
/// Converts leaf member values to Consul KV bytes. Strings are stored raw as UTF-8;
/// other scalars use invariant text and complex values use JSON.
/// </summary>
internal static class ConsulValueConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public static byte[] Encode(object? value, Type targetType)
    {
        if (value is null)
        {
            return [];
        }

        if (value is string text)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        if (value is byte[] bytes)
        {
            return bytes;
        }

        var valueType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (valueType == typeof(bool))
        {
            return Encoding.UTF8.GetBytes(
                ((bool)value).ToString(CultureInfo.InvariantCulture).ToLowerInvariant()
            );
        }

        if (value is IConvertible)
        {
            return Encoding.UTF8.GetBytes(
                Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            );
        }

        if (valueType == typeof(Guid))
        {
            return Encoding.UTF8.GetBytes(value.ToString() ?? string.Empty);
        }

        if (valueType == typeof(Uri))
        {
            return Encoding.UTF8.GetBytes(value.ToString() ?? string.Empty);
        }

        if (valueType == typeof(Version))
        {
            return Encoding.UTF8.GetBytes(value.ToString() ?? string.Empty);
        }

#if !NETSTANDARD
        if (valueType == typeof(DateOnly) || valueType == typeof(TimeOnly))
        {
            return Encoding.UTF8.GetBytes(value.ToString() ?? string.Empty);
        }
#endif
        try
        {
            return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, valueType, JsonOptions));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new FormatException(
                $"The value for type '{targetType}' cannot be encoded for Consul KV.",
                exception
            );
        }
    }

    public static object? Decode(ReadOnlyMemory<byte> content, Type targetType, string consulKey)
    {
        var text = Encoding.UTF8.GetString(content.Span);
        var nullableType = Nullable.GetUnderlyingType(targetType);
        var valueType = nullableType ?? targetType;
        if (nullableType is not null && text.Length == 0)
        {
            return null;
        }

        try
        {
            if (valueType == typeof(string))
            {
                return text;
            }

            if (valueType == typeof(byte[]))
            {
                return content.ToArray();
            }

            if (valueType == typeof(bool))
            {
                return bool.Parse(text);
            }

            if (valueType == typeof(char))
            {
                if (text.Length != 1)
                {
                    throw new FormatException(
                        "A character value must contain exactly one character."
                    );
                }

                return text[0];
            }

            if (valueType.IsEnum)
            {
                return Enum.Parse(valueType, text, ignoreCase: true);
            }

            if (valueType == typeof(Guid))
            {
                return Guid.Parse(text);
            }

            if (valueType == typeof(DateTime))
            {
                return DateTime.Parse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind
                );
            }

            if (valueType == typeof(DateTimeOffset))
            {
                return DateTimeOffset.Parse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind
                );
            }
#if !NETSTANDARD
            if (valueType == typeof(DateOnly))
            {
                return DateOnly.Parse(text, CultureInfo.InvariantCulture);
            }

            if (valueType == typeof(TimeOnly))
            {
                return TimeOnly.Parse(text, CultureInfo.InvariantCulture);
            }
#endif
            if (valueType == typeof(TimeSpan))
            {
                return TimeSpan.Parse(text, CultureInfo.InvariantCulture);
            }

            if (valueType == typeof(Uri))
            {
                return new Uri(text, UriKind.RelativeOrAbsolute);
            }

            if (valueType == typeof(Version))
            {
                return Version.Parse(text);
            }

            if (typeof(IConvertible).IsAssignableFrom(valueType))
            {
                return Convert.ChangeType(text, valueType, CultureInfo.InvariantCulture);
            }

            if (valueType == typeof(object))
            {
                return text;
            }

            return JsonSerializer.Deserialize(text, valueType, JsonOptions);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new FormatException(
                $"The Consul key '{consulKey}' is not a valid value for type '{targetType}'.",
                exception
            );
        }
    }
}
