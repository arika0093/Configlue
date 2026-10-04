using System.Globalization;
using System.Text.Json;

namespace Configlue.Resource.AzureAppConfiguration;

/// <summary>Converts App Configuration string values to model member types.</summary>
internal static class AzureAppConfigurationValueConverter
{
    public static object? Parse(
        string value,
        Type targetType,
        string memberPath,
        string configurationKey,
        JsonSerializerOptions? jsonOptions = null,
        Func<string, Type, object?>? valueParser = null
    )
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(targetType);
        if (valueParser is not null)
        {
            try
            {
                return valueParser(value, targetType);
            }
            catch (NotSupportedException)
            {
                // Fall through to the built-in conversion, then JSON.
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new FormatException(
                    $"App Configuration key '{configurationKey}' is not a valid value for member '{memberPath}' of type '{targetType}'.",
                    exception
                );
            }
        }

        var nullableType = Nullable.GetUnderlyingType(targetType);
        var valueType = nullableType ?? targetType;
        if (nullableType is not null && value.Length == 0)
        {
            return null;
        }

        try
        {
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
#else
            if (valueType.FullName == "System.DateOnly" || valueType.FullName == "System.TimeOnly")
            {
                var parse = valueType.GetMethod(
                    "Parse",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    binder: null,
                    types: [typeof(string), typeof(IFormatProvider)],
                    modifiers: null
                );
                if (parse is not null)
                {
                    return parse.Invoke(null, [value, CultureInfo.InvariantCulture]);
                }
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
        }
        catch (Exception exception)
            when (exception is FormatException or OverflowException or ArgumentException)
        {
            throw new FormatException(
                $"App Configuration key '{configurationKey}' is not a valid value for member '{memberPath}' of type '{targetType}'.",
                exception
            );
        }

        try
        {
            if (valueType == typeof(object))
            {
                return value;
            }

            return JsonSerializer.Deserialize(value, valueType, jsonOptions);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new FormatException(
                $"App Configuration key '{configurationKey}' is not valid JSON for member '{memberPath}' of type '{targetType}'.",
                exception
            );
        }
    }

    public static string Render(
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
        if (valueType == typeof(string))
        {
            return value.ToString() ?? string.Empty;
        }

        if (value is bool boolean)
        {
            return boolean ? "true" : "false";
        }

        if (value is char character)
        {
            return character.ToString();
        }

        if (value is Guid guid)
        {
            return guid.ToString("D", CultureInfo.InvariantCulture);
        }

        if (value is DateTime dateTime)
        {
            return dateTime.ToString("O", CultureInfo.InvariantCulture);
        }

        if (value is DateTimeOffset dateTimeOffset)
        {
            return dateTimeOffset.ToString("O", CultureInfo.InvariantCulture);
        }

#if !NETSTANDARD
        if (value is DateOnly dateOnly)
        {
            return dateOnly.ToString("O", CultureInfo.InvariantCulture);
        }

        if (value is TimeOnly timeOnly)
        {
            return timeOnly.ToString("O", CultureInfo.InvariantCulture);
        }
#endif
        if (value is TimeSpan timeSpan)
        {
            return timeSpan.ToString("c", CultureInfo.InvariantCulture);
        }

        if (value is byte[] bytes)
        {
            return Convert.ToBase64String(bytes);
        }

        if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(valueType))
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        if (valueType.IsInstanceOfType(value))
        {
            var text = value.ToString();
            if (text is not null && value.GetType().IsPrimitive)
            {
                return text;
            }
        }

        return JsonSerializer.Serialize(value, value.GetType(), jsonOptions);
    }
}
