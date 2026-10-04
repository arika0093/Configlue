using System.Globalization;
using System.Text.Json;

namespace Configlue.Source.Ssm;

internal static class SsmValueConverter
{
    public static object? Parse(
        string raw,
        bool isStringList,
        Type targetType,
        Func<string, Type, object?> valueParser,
        JsonSerializerOptions? jsonOptions
    )
    {
        if (isStringList && TryGetStringCollection(targetType, out _))
        {
            var items = raw.Split(',').Select(static item => item.Trim()).ToArray();
            return ConvertStringCollection(items, targetType);
        }

        object? parsed;
        try
        {
            parsed = valueParser(raw, targetType);
        }
        catch (NotSupportedException exception)
        {
            parsed = ParseJson(raw, targetType, jsonOptions, exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new FormatException(
                $"The parameter value is not a valid '{targetType}' value.",
                exception
            );
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

    public static object? ParseDefault(string raw, Type targetType)
    {
        return DefaultScalar(raw, targetType);
    }

    private static bool TryGetStringCollection(Type targetType, out bool isStringElement)
    {
        isStringElement = false;
        var element = GetCollectionElementType(targetType);
        if (element is null)
        {
            return false;
        }

        isStringElement = element == typeof(string);
        return isStringElement;
    }

    private static Type? GetCollectionElementType(Type targetType)
    {
        if (targetType.IsArray)
        {
            return targetType.GetElementType();
        }

        if (!targetType.IsGenericType)
        {
            return null;
        }

        var args = targetType.GetGenericArguments();
        return args.Length == 1 ? args[0] : null;
    }

    private static object? ConvertStringCollection(string[] items, Type targetType)
    {
        if (targetType.IsArray && targetType.GetElementType() == typeof(string))
        {
            return items.ToArray();
        }

        if (targetType == typeof(List<string>))
        {
            return items.ToList();
        }

        if (
            targetType.IsGenericType
            && targetType.GetGenericTypeDefinition() == typeof(HashSet<string>)
        )
        {
            return new HashSet<string>(items);
        }

        // Fall back to JSON for other string-collection shapes so generated
        // collection factories keep working without per-shape special cases.
        var json = JsonSerializer.Serialize(items);
        return JsonSerializer.Deserialize(json, targetType);
    }

    private static object? ParseJson(
        string raw,
        Type targetType,
        JsonSerializerOptions? options,
        Exception? declinedBy
    )
    {
        if (targetType == typeof(object))
        {
            return raw;
        }

        try
        {
            return JsonSerializer.Deserialize(raw, targetType, options);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new FormatException(
                $"The value is not valid JSON for type '{targetType}'. Supply a value parser for this type.",
                declinedBy is null ? exception : new AggregateException(declinedBy, exception)
            );
        }
    }

    private static object? DefaultScalar(string value, Type targetType)
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
                    "A character parameter value must contain exactly one character."
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

    public static string Format(object? value, JsonSerializerOptions? jsonOptions)
    {
        if (value is null)
        {
            throw new InvalidOperationException(
                "Parameter Store string mapping does not support null values. Remove the member instead."
            );
        }

        if (value is string text)
        {
            return text;
        }

        if (value is bool flag)
        {
            return flag ? "true" : "false";
        }

        if (value is IConvertible convertible && value.GetType().IsPrimitive)
        {
            return convertible.ToString(CultureInfo.InvariantCulture);
        }

        if (value is Enum)
        {
            return value.ToString() ?? string.Empty;
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
        if (value is TimeSpan span)
        {
            return span.ToString("c", CultureInfo.InvariantCulture);
        }

        if (value is Uri uri)
        {
            return uri.ToString();
        }

        if (value is Version version)
        {
            return version.ToString();
        }

        if (value is byte[] bytes)
        {
            return Convert.ToBase64String(bytes);
        }

        if (value is System.Collections.IEnumerable)
        {
            return JsonSerializer.Serialize(value, value.GetType(), jsonOptions);
        }

        return JsonSerializer.Serialize(value, value.GetType(), jsonOptions);
    }

    public static string FormatStringList(object? value)
    {
        if (value is string text)
        {
            return text;
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            var items = new List<string>();
            foreach (var item in enumerable)
            {
                var formatted = item?.ToString() ?? string.Empty;
                if (formatted.Contains(','))
                {
                    throw new FormatException("StringList elements cannot contain commas.");
                }

                items.Add(formatted.Trim());
            }

            return string.Join(",", items);
        }

        return Format(value, null);
    }
}
