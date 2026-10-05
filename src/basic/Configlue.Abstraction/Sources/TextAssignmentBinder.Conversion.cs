using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Configlue.Sources;

internal static partial class TextAssignmentBinder
{
    internal static object? ConvertValue(
        object? raw,
        Type targetType,
        string path,
        string origin,
        TextAssignmentBinderOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(targetType);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        ArgumentNullException.ThrowIfNull(options);

        if (raw is null)
        {
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null)
            {
                throw new FormatException(
                    $"The value for model path '{path}' from '{origin}' is null but requires '{targetType}'."
                );
            }

            return null;
        }

        var valueType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (valueType.IsInstanceOfType(raw))
        {
            return raw;
        }

        if (valueType == typeof(string))
        {
            return raw is IConvertible convertible
                ? System.Convert.ToString(convertible, CultureInfo.InvariantCulture)
                : raw.ToString();
        }

        if (raw is string text)
        {
            return ConvertText(text, targetType, valueType, path, origin, options);
        }

        if (valueType.IsEnum && IsIntegral(raw))
        {
            return Enum.ToObject(valueType, raw);
        }

        if (
            raw is IConvertible
            && typeof(IConvertible).IsAssignableFrom(valueType)
            && valueType != typeof(object)
        )
        {
            try
            {
                return System.Convert.ChangeType(raw, valueType, CultureInfo.InvariantCulture);
            }
            catch (Exception exception)
                when (exception is FormatException or InvalidCastException or OverflowException)
            {
                throw ConversionFailure(path, origin, raw, targetType, exception);
            }
        }

        if (raw is IEnumerable sequence)
        {
            return ConvertSequence(sequence, valueType, targetType, path, origin, options);
        }

        throw ConversionFailure(path, origin, raw, targetType, null);
    }

    private static object? ConvertText(
        string text,
        Type targetType,
        Type valueType,
        string path,
        string origin,
        TextAssignmentBinderOptions options
    )
    {
        if (options.TextParser is not null)
        {
            object? parsed;
            try
            {
                parsed = options.TextParser(text, targetType);
            }
            catch (NotSupportedException declined)
            {
                return ConvertDefaultText(
                    text,
                    valueType,
                    targetType,
                    path,
                    origin,
                    options,
                    declined
                );
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new FormatException(
                    $"The value from '{origin}' is not a valid value for member '{path}' of type '{targetType}'.",
                    exception
                );
            }

            if (parsed is null)
            {
                if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) is null)
                {
                    throw new FormatException(
                        $"The parser from '{origin}' returned null for non-nullable member '{path}'."
                    );
                }

                return null;
            }

            if (!valueType.IsInstanceOfType(parsed))
            {
                throw new FormatException(
                    $"The parser from '{origin}' returned '{parsed.GetType()}' for member '{path}', which requires '{targetType}'."
                );
            }

            return parsed;
        }

        return ConvertDefaultText(text, valueType, targetType, path, origin, options, null);
    }

    private static object? ConvertDefaultText(
        string text,
        Type valueType,
        Type targetType,
        string path,
        string origin,
        TextAssignmentBinderOptions options,
        Exception? declinedBy
    )
    {
        if (Nullable.GetUnderlyingType(targetType) is not null && text.Length == 0)
        {
            return null;
        }

        try
        {
            if (valueType == typeof(bool))
            {
                return bool.Parse(text);
            }

            if (valueType == typeof(char))
            {
                if (text.Length == 1)
                {
                    return text[0];
                }

                throw new FormatException(
                    $"The value for model path '{path}' from '{origin}' must contain exactly one character."
                );
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
#else
            if (valueType.FullName == "System.DateOnly" || valueType.FullName == "System.TimeOnly")
            {
                var parse = valueType.GetMethod(
                    "Parse",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: [typeof(string), typeof(IFormatProvider)],
                    modifiers: null
                );
                if (parse is not null)
                {
                    return parse.Invoke(null, [text, CultureInfo.InvariantCulture]);
                }
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

            if (valueType == typeof(byte[]))
            {
                return System.Convert.FromBase64String(text);
            }

            if (typeof(IConvertible).IsAssignableFrom(valueType))
            {
                return System.Convert.ChangeType(text, valueType, CultureInfo.InvariantCulture);
            }
        }
        catch (Exception exception)
            when (exception is FormatException or OverflowException or ArgumentException)
        {
            throw ConversionFailure(path, origin, text, targetType, exception);
        }

        return ParseJson(text, valueType, path, origin, options, declinedBy);
    }

    private static object? ParseJson(
        string text,
        Type valueType,
        string path,
        string origin,
        TextAssignmentBinderOptions options,
        Exception? declinedBy
    )
    {
        if (valueType == typeof(object))
        {
            return text;
        }

        try
        {
            return JsonSerializer.Deserialize(text, valueType, options.JsonOptions);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var inner = declinedBy is null
                ? exception
                : new AggregateException(declinedBy, exception);
            throw new FormatException(
                $"The value for model path '{path}' from '{origin}' is not valid JSON for type '{valueType}'. Supply a value parser for this type.",
                inner
            );
        }
    }

    private static object ConvertSequence(
        IEnumerable sequence,
        Type valueType,
        Type targetType,
        string path,
        string origin,
        TextAssignmentBinderOptions options
    )
    {
        if (valueType.IsArray && valueType.GetArrayRank() == 1)
        {
            var elementType = valueType.GetElementType()!;
            var items = sequence
                .Cast<object?>()
                .Select(item => ConvertValue(item, elementType, path, origin, options))
                .ToArray();
            var array = Array.CreateInstance(elementType, items.Length);
            Array.Copy(items, array, items.Length);
            return array;
        }

        if (
            valueType.IsGenericType && valueType.GetGenericArguments() is { Length: 1 } elementTypes
        )
        {
            var elementType = elementTypes[0];
            var items = sequence
                .Cast<object?>()
                .Select(item => ConvertValue(item, elementType, path, origin, options))
                .ToArray();
            var definition = valueType.GetGenericTypeDefinition();
            if (
                definition == typeof(List<>)
                || definition == typeof(IList<>)
                || definition == typeof(ICollection<>)
                || definition == typeof(IEnumerable<>)
                || definition == typeof(IReadOnlyList<>)
                || definition == typeof(IReadOnlyCollection<>)
            )
            {
                return CreateList(elementType, items, path, origin, valueType);
            }

            if (definition == typeof(HashSet<>) || definition == typeof(ISet<>))
            {
                return CreateSet(elementType, items, path, origin, valueType);
            }
        }

        if (
            valueType.IsGenericType
            && valueType.GetGenericArguments() is { Length: 2 }
            && sequence is IDictionary dictionary
        )
        {
            var definition = valueType.GetGenericTypeDefinition();
            if (
                definition == typeof(Dictionary<,>)
                || definition == typeof(IDictionary<,>)
                || definition == typeof(IReadOnlyDictionary<,>)
            )
            {
                return CreateDictionary(valueType, dictionary, path, origin, options);
            }
        }

        throw ConversionFailure(path, origin, sequence, targetType, null);
    }

    [RequiresUnreferencedCode(
        "Concrete collection construction reflects over generic instantiations that trimming may remove."
    )]
    [RequiresDynamicCode("Concrete collection construction may require runtime code generation.")]
    private static object CreateList(
        Type elementType,
        object?[] items,
        string path,
        string origin,
        Type valueType
    )
    {
        try
        {
            var list = (IList)
                Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
            foreach (var item in items)
            {
                list.Add(item);
            }

            return list;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw ConversionFailure(path, origin, items, valueType, exception);
        }
    }

    [RequiresUnreferencedCode(
        "Concrete collection construction reflects over generic instantiations that trimming may remove."
    )]
    [RequiresDynamicCode("Concrete collection construction may require runtime code generation.")]
    private static object CreateSet(
        Type elementType,
        object?[] items,
        string path,
        string origin,
        Type valueType
    )
    {
        try
        {
            var set = Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(elementType))!;
            var add = set.GetType().GetMethod("Add", [elementType])!;
            foreach (var item in items)
            {
                add.Invoke(set, [item]);
            }

            return set;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw ConversionFailure(path, origin, items, valueType, exception);
        }
    }

    [RequiresUnreferencedCode(
        "Concrete collection construction reflects over generic instantiations that trimming may remove."
    )]
    [RequiresDynamicCode("Concrete collection construction may require runtime code generation.")]
    private static object CreateDictionary(
        Type valueType,
        IDictionary dictionary,
        string path,
        string origin,
        TextAssignmentBinderOptions options
    )
    {
        try
        {
            var created = (IDictionary)
                Activator.CreateInstance(
                    typeof(Dictionary<,>).MakeGenericType(valueType.GetGenericArguments())
                )!;
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = ConvertValue(
                    entry.Key,
                    valueType.GetGenericArguments()[0],
                    path,
                    origin,
                    options
                );
                if (key is null)
                {
                    throw ConversionFailure(path, origin, entry.Key, valueType, null);
                }

                created.Add(
                    key,
                    ConvertValue(
                        entry.Value,
                        valueType.GetGenericArguments()[1],
                        path,
                        origin,
                        options
                    )
                );
            }

            return created;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw ConversionFailure(path, origin, dictionary, valueType, exception);
        }
    }

    private static bool IsIntegral(object? value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong;

    private static FormatException ConversionFailure(
        string path,
        string origin,
        object? raw,
        Type targetType,
        Exception? inner
    ) =>
        new(
            $"The value for model path '{path}' from '{origin}' cannot be converted from '{raw?.GetType()}' to '{targetType}'. Map the value with a converter for custom shapes.",
            inner
        );
}
