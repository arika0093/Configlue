using System.Globalization;
using System.Text.Json;

namespace Configlue.Codecs;

/// <summary>
/// Standard scalar text conversion shared by keyed secret providers.
/// </summary>
/// <remarks>
/// <para>
/// Converts between UTF-8 secret text and model leaf values. Types with a scalar
/// conversion (primitives, enums, GUIDs, dates, URIs, versions, byte arrays) use
/// invariant text; other types fall back to JSON. A custom parser may decline a type
/// with <see cref="NotSupportedException"/> to trigger the JSON fallback.
/// </para>
/// <para>Messages never include the converted value.</para>
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public static class ScalarTextConverter
{
    /// <summary>Parses secret text into a leaf value, with JSON fallback.</summary>
    public static object? Parse(
        string text,
        Type targetType,
        Func<string, Type, object?>? valueParser = null,
        JsonSerializerOptions? jsonOptions = null
    )
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(targetType);
        object? parsed;
        try
        {
            parsed = valueParser is not null
                ? valueParser(text, targetType)
                : ParseScalar(text, targetType);
        }
        catch (NotSupportedException exception)
        {
            parsed = ParseJson(text, targetType, jsonOptions, exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new FormatException($"The value is not a valid '{targetType}' value.", exception);
        }

        var valueType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (parsed is null)
        {
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null)
            {
                throw new FormatException("The parser returned null for a non-nullable member.");
            }

            return null;
        }

        if (!valueType.IsInstanceOfType(parsed))
        {
            throw new FormatException(
                $"The parser returned '{parsed.GetType()}' but the member requires '{targetType}'."
            );
        }

        return parsed;
    }

    /// <summary>Default scalar conversion used when no custom parser is supplied.</summary>
    public static object? ParseDefault(string text, Type targetType) =>
        ParseScalar(text, targetType);

    /// <summary>Formats a leaf value as secret text, using JSON for complex values.</summary>
    public static string Format(
        object? value,
        Type targetType,
        JsonSerializerOptions? jsonOptions = null
    )
    {
        if (value is null)
        {
            return string.Empty;
        }

        var valueType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (valueType == typeof(string) && value is string text)
        {
            return text;
        }

        if (valueType == typeof(bool) && value is bool flag)
        {
            return flag ? "true" : "false";
        }

        if (valueType == typeof(byte[]) && value is byte[] bytes)
        {
            return Convert.ToBase64String(bytes);
        }

        if (value is IConvertible)
        {
            if (value is DateTime dateTime)
            {
                return dateTime.ToString("O", CultureInfo.InvariantCulture);
            }

            if (value is DateTimeOffset dateOffset)
            {
                return dateOffset.ToString("O", CultureInfo.InvariantCulture);
            }

            if (value is IFormattable formattable)
            {
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return JsonSerializer.Serialize(value, valueType, jsonOptions);
    }

    private static object? ParseScalar(string value, Type targetType)
    {
        var nullableType = Nullable.GetUnderlyingType(targetType);
        var valueType = nullableType ?? targetType;
        if (nullableType is not null && value.Length == 0)
        {
            return null;
        }

        if (valueType == typeof(string))
        {
            return value;
        }

        if (valueType == typeof(bool))
        {
            return bool.Parse(value);
        }

        if (valueType == typeof(char))
        {
            return value.Length == 1
                ? value[0]
                : throw new FormatException(
                    "A character value must contain exactly one character."
                );
        }

        if (valueType.IsEnum)
        {
            return Enum.Parse(valueType, value, ignoreCase: true);
        }

        if (valueType == typeof(Guid))
        {
            return Guid.Parse(value);
        }

        if (valueType == typeof(DateTime))
        {
            return DateTime.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind
            );
        }

        if (valueType == typeof(DateTimeOffset))
        {
            return DateTimeOffset.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind
            );
        }
#if !NETSTANDARD
        if (valueType == typeof(DateOnly))
        {
            return DateOnly.Parse(value, CultureInfo.InvariantCulture);
        }

        if (valueType == typeof(TimeOnly))
        {
            return TimeOnly.Parse(value, CultureInfo.InvariantCulture);
        }
#endif
        if (valueType == typeof(TimeSpan))
        {
            return TimeSpan.Parse(value, CultureInfo.InvariantCulture);
        }

        if (valueType == typeof(Uri))
        {
            return new Uri(value, UriKind.RelativeOrAbsolute);
        }

        if (valueType == typeof(Version))
        {
            return Version.Parse(value);
        }

        if (valueType == typeof(byte[]))
        {
            return Convert.FromBase64String(value);
        }

        if (typeof(IConvertible).IsAssignableFrom(valueType))
        {
            return Convert.ChangeType(value, valueType, CultureInfo.InvariantCulture);
        }

        return ParseJson(value, valueType, null, null);
    }

    private static object? ParseJson(
        string value,
        Type valueType,
        JsonSerializerOptions? options,
        Exception? declinedBy
    )
    {
        if (valueType == typeof(object))
        {
            return value;
        }

        try
        {
            return JsonSerializer.Deserialize(value, valueType, options);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new FormatException(
                $"The value is not valid JSON for type '{valueType}'. Supply a value parser for this type.",
                declinedBy is null ? exception : new AggregateException(declinedBy, exception)
            );
        }
    }
}
