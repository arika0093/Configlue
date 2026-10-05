using System.Buffers;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Configlue.CompilerServices;

namespace Configlue.Sources;

/// <summary>One normalized flat assignment accepted by the shared schema-aware binder.</summary>
/// <remarks>
/// <c>PathSegments</c> are raw member-name segments (for example split on <c>__</c> or
/// <c>.</c>); resolution is case-insensitive. <c>RawValue</c> is the transport value
/// (text for environment variables, parsed objects for command-line symbols).
/// <c>Origin</c> names the transport key (environment key or symbol name) for
/// diagnostics and for revision inputs that resolve to no member.
/// </remarks>
internal readonly record struct TextAssignment(
    string[] PathSegments,
    object? RawValue,
    string Origin
);

/// <summary>How the shared binder resolves several assignments targeting one member.</summary>
internal enum TextAssignmentDuplicatePolicy
{
    /// <summary>Reject duplicates.</summary>
    Throw,

    /// <summary>Later assignments win.</summary>
    LastWins,
}

/// <summary>Hooks customizing the shared text-assignment binder per transport.</summary>
internal sealed class TextAssignmentBinderOptions
{
    /// <summary>
    /// Optional text parser hook (for example environment scalar overrides).
    /// Invoked only for string raw values. Throw <see cref="NotSupportedException"/>
    /// to decline and fall back to the shared scalar/JSON conversion.
    /// </summary>
    public Func<string, Type, object?>? TextParser { get; init; }

    /// <summary>JSON options for structured members bound from text.</summary>
    public JsonSerializerOptions? JsonOptions { get; init; }

    /// <summary>How duplicate canonical targets are resolved.</summary>
    public TextAssignmentDuplicatePolicy DuplicatePolicy { get; init; } =
        TextAssignmentDuplicatePolicy.Throw;
}

/// <summary>Result of binding flat assignments into a sparse fragment.</summary>
/// <remarks>
/// <c>Revision</c> follows the deterministic binder revision specified on
/// <see cref="TextAssignmentBinder"/>:
/// converted (not raw) values for matched members plus raw values for unmatched
/// origins. Two binds with equal logical content share a revision even when
/// their transports differ (text versus typed scalars, JSON list versus typed
/// array) or their input order differs.
/// </remarks>
internal readonly record struct BoundTextFragment(
    IConfiglueFragment Fragment,
    bool MatchedAny,
    string Revision
);

/// <summary>
/// Shared schema-aware binder for flat key/value transports (environment, command line).
/// Owns member-path resolution, nested fragment construction, scalar/JSON/collection
/// conversion, duplicate handling, and deterministic revision construction.
/// Transports own naming/symbol extraction only; custom parsers are hooks here.
/// Supports only Configlue's documented model shapes.
/// </summary>
/// <remarks>
/// Transport contract: transports pass raw member-name segments without resolving
/// them (environment splits prefixed keys on "__" and maps explicit names;
/// command line passes registration-validated mapping paths) and select a
/// duplicate policy (environment rejects duplicates, command line lets later
/// mappings win). The binder owns canonical case-insensitive resolution,
/// conversion, deduplication by canonical path, and revision, so transports
/// must not pre-resolve or pre-deduplicate.
/// <para>
/// Conversion rules (intentional unification of the legacy transports):
/// values already assignable to the target type pass through untouched;
/// string values for scalar targets use invariant-culture scalar parsing while
/// strings for collection/object targets fall back to JSON deserialization.
/// The JSON fallback is new for command-line transports (which previously
/// rejected such text); it is intentional so both transports share one matrix.
/// All conversion failures throw <see cref="FormatException"/> shaped
/// "The value for model path '{path}' from '{origin}' ..." regardless of
/// transport, replacing the legacy "Environment variable ..." and
/// "The command-line value ..." prefixes.
/// </para>
/// <para>
/// Unknown path segments are unmatched rather than errors: binding ignores them
/// but folds their origins into the revision, and a fully unmatched bind yields
/// <c>MatchedAny == false</c> (readers surface that as NotFound). This matches
/// the environment transport's legacy behavior; for command line it is
/// unreachable via the public API because mapping paths are validated at
/// registration, so the legacy throw for unknown segments was dead code.
/// </para>
/// </remarks>
internal static class TextAssignmentBinder
{
    /// <summary>Binds flat assignments into a sparse fragment.</summary>
    /// <remarks>
    /// Revision is a deterministic SHA256 (uppercase hex) over length-prefixed
    /// entries: matched entries as (canonical dotted path,
    /// rendered converted value) sorted with ordinal comparison, then unmatched
    /// entries as (origin, rendered raw value) sorted by origin.
    /// The converted-value basis is an intentional spec change from the legacy
    /// transports (environment hashed upper-cased keys with raw text; command
    /// line hashed raw parsed values): text "8" and typed 8 bound to one member
    /// now share a revision, and concrete collection identity is erased
    /// (a JSON list and a typed array with equal elements share a revision).
    /// Unmatched origins are included so callers observe underlying changes
    /// even when nothing binds. Input order does not affect the revision.
    /// </remarks>
    public static BoundTextFragment Bind(
        ConfiglueModelSchema schema,
        IReadOnlyList<TextAssignment> assignments,
        TextAssignmentBinderOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(assignments);
        options ??= new TextAssignmentBinderOptions();

        var lookups = new Dictionary<ConfiglueModelSchema, TextAssignmentMemberLookup>();
        CollectLookups(schema, new HashSet<Type>(), lookups);

        // Resolve to canonical paths; unknown members are unmatched (ignored for
        // binding but included in the revision so callers observe underlying changes).
        var resolved = new List<ResolvedAssignment>(assignments.Count);
        var unmatched = new List<TextAssignment>();
        foreach (var assignment in assignments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(assignment.PathSegments);
            ArgumentException.ThrowIfNullOrWhiteSpace(assignment.Origin);
            if (assignment.PathSegments.Length == 0)
            {
                unmatched.Add(assignment);
                continue;
            }

            var canonical = ResolveCanonicalPath(
                schema,
                assignment.PathSegments,
                assignment.Origin,
                lookups
            );
            if (canonical is null)
            {
                unmatched.Add(assignment);
                continue;
            }

            resolved.Add(new ResolvedAssignment(canonical, assignment.RawValue, assignment.Origin));
        }

        // Deduplicate by canonical path.
        var deduped = new Dictionary<string, ResolvedAssignment>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var item in resolved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = string.Join(".", item.CanonicalPath);
            if (deduped.TryGetValue(key, out var existing))
            {
                if (options.DuplicatePolicy == TextAssignmentDuplicatePolicy.Throw)
                {
                    throw new InvalidOperationException(
                        $"More than one value maps to model property '{key}' ('{existing.Origin}' and '{item.Origin}')."
                    );
                }

                deduped[key] = item;
            }
            else
            {
                deduped.Add(key, item);
                order.Add(key);
            }
        }

        order.Sort(StringComparer.Ordinal);

        IConfiglueFragment fragment = schema.CreateEmptyFragment();
        var matchedAny = false;
        var convertedForRevision = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = deduped[key];
            var applied = SetValue(
                fragment,
                schema,
                item.CanonicalPath,
                0,
                item.RawValue,
                item.Origin,
                options,
                lookups,
                cancellationToken
            );
            fragment = applied.Fragment;
            if (applied.Matched)
            {
                matchedAny = true;
                convertedForRevision[key] = applied.ConvertedValue;
            }
            else
            {
                unmatched.Add(new TextAssignment(item.CanonicalPath, item.RawValue, item.Origin));
            }
        }

        var revision = CreateRevision(convertedForRevision, unmatched, cancellationToken);
        return new BoundTextFragment(fragment, matchedAny, revision);
    }

    private static void CollectLookups(
        ConfiglueModelSchema schema,
        HashSet<Type> ancestors,
        Dictionary<ConfiglueModelSchema, TextAssignmentMemberLookup> lookups
    )
    {
        if (!ancestors.Add(schema.ModelType))
        {
            return;
        }

        lookups[schema] = TextAssignmentMemberLookup.Create(schema);
        foreach (
            var nestedFactory in schema
                .Members.Select(static member => member.NestedSchemaFactory)
                .OfType<Func<ConfiglueModelSchema>>()
        )
        {
            CollectLookups(nestedFactory(), ancestors, lookups);
        }

        ancestors.Remove(schema.ModelType);
    }

    private static string[]? ResolveCanonicalPath(
        ConfiglueModelSchema schema,
        string[] path,
        string origin,
        Dictionary<ConfiglueModelSchema, TextAssignmentMemberLookup> lookups
    )
    {
        var canonical = new string[path.Length];
        var current = schema;
        for (var index = 0; index < path.Length; index++)
        {
            if (!lookups.TryGetValue(current, out var lookup))
            {
                lookup = TextAssignmentMemberLookup.Create(current);
                lookups[current] = lookup;
            }

            if (!lookup.TryResolve(path[index], out var member, out var isAmbiguous))
            {
                if (isAmbiguous)
                {
                    throw new FormatException(
                        $"Path segment '{path[index]}' from '{origin}' is ambiguous in schema '{current.Id}'."
                    );
                }

                return null;
            }

            canonical[index] = member.Name;
            if (index == path.Length - 1)
            {
                return canonical;
            }

            if (member.NestedSchemaFactory is null)
            {
                throw new FormatException(
                    $"Path '{string.Join(".", path)}' from '{origin}' continues past non-nested member '{member.Name}'."
                );
            }

            current = member.NestedSchemaFactory();
        }

        return null;
    }

    private sealed record ResolvedAssignment(
        string[] CanonicalPath,
        object? RawValue,
        string Origin
    );

    private readonly record struct AppliedFragment(
        IConfiglueFragment Fragment,
        bool Matched,
        object? ConvertedValue
    );

    private static AppliedFragment SetValue(
        IConfiglueFragment fragment,
        ConfiglueModelSchema schema,
        string[] path,
        int pathIndex,
        object? rawValue,
        string origin,
        TextAssignmentBinderOptions options,
        Dictionary<ConfiglueModelSchema, TextAssignmentMemberLookup> lookups,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!lookups[schema].TryResolve(path[pathIndex], out var member, out var isAmbiguous))
        {
            if (isAmbiguous)
            {
                throw new FormatException(
                    $"Path segment '{path[pathIndex]}' from '{origin}' is ambiguous in schema '{schema.Id}'."
                );
            }

            return new AppliedFragment(fragment, false, null);
        }

        if (pathIndex == path.Length - 1)
        {
            if (member.NestedSchemaFactory is not null)
            {
                throw new FormatException(
                    $"The value from '{origin}' names nested model '{member.Name}'. Use additional segments to set its members."
                );
            }

            var converted = ConvertValue(
                rawValue,
                member.ValueType,
                string.Join(".", path),
                origin,
                options
            );
            return new AppliedFragment(fragment.WithMember(member.Id, converted), true, converted);
        }

        if (member.NestedSchemaFactory is null)
        {
            throw new FormatException(
                $"Path '{string.Join(".", path)}' from '{origin}' continues past non-nested member '{member.Name}'."
            );
        }

        var nestedSchema = member.NestedSchemaFactory();
        var nestedFragment =
            FindPresentMember(fragment, member.Id) as IConfiglueFragment
            ?? nestedSchema.CreateEmptyFragment();
        var nested = SetValue(
            nestedFragment,
            nestedSchema,
            path,
            pathIndex + 1,
            rawValue,
            origin,
            options,
            lookups,
            cancellationToken
        );
        return nested.Matched
            ? new AppliedFragment(
                fragment.WithMember(member.Id, nested.Fragment),
                true,
                nested.ConvertedValue
            )
            : new AppliedFragment(fragment, false, null);
    }

    private static object? FindPresentMember(IConfiglueFragment fragment, int memberId)
    {
        foreach (var member in fragment.EnumeratePresentMembers())
        {
            if (member.Id == memberId)
            {
                return member.Value;
            }
        }

        return null;
    }

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

    /// <summary>Renders a converted or raw value for deterministic revision hashing.</summary>
    /// <remarks>
    /// Rendering is logical, not concrete: sequences render as
    /// "[element,...]" and dictionaries as "{key=value,...}" without the
    /// concrete collection type name, so a JSON-deserialized
    /// <c>List&lt;T&gt;</c> and a typed <c>T[]</c> with equal elements hash
    /// equally. Scalar rendering keeps the concrete type name.
    /// </remarks>
    internal static string Render(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        var type = value.GetType();
        if (value is string text)
        {
            return $"{type.FullName}:\"{text}\"";
        }

        if (value is bool boolean)
        {
            return boolean ? $"{type.FullName}:true" : $"{type.FullName}:false";
        }

        if (value is char character)
        {
            return $"{type.FullName}:{character}";
        }

        if (value is DateTime dateTime)
        {
            return $"{type.FullName}:{dateTime.ToString("O", CultureInfo.InvariantCulture)}";
        }

        if (value is DateTimeOffset dateTimeOffset)
        {
            return $"{type.FullName}:{dateTimeOffset.ToString("O", CultureInfo.InvariantCulture)}";
        }

#if !NETSTANDARD
        if (value is DateOnly dateOnly)
        {
            return $"{type.FullName}:{dateOnly.ToString("O", CultureInfo.InvariantCulture)}";
        }

        if (value is TimeOnly timeOnly)
        {
            return $"{type.FullName}:{timeOnly.ToString("O", CultureInfo.InvariantCulture)}";
        }
#else
        if (
            (type.FullName == "System.DateOnly" || type.FullName == "System.TimeOnly")
            && value is IFormattable dateTimeText
        )
        {
            return $"{type.FullName}:{dateTimeText.ToString("O", CultureInfo.InvariantCulture)}";
        }
#endif

        if (value is TimeSpan timeSpan)
        {
            return $"{type.FullName}:{timeSpan.ToString("c", CultureInfo.InvariantCulture)}";
        }

        if (value is Guid guid)
        {
            return $"{type.FullName}:{guid:D}";
        }

        if (value is byte[] bytes)
        {
            return $"{type.FullName}:{System.Convert.ToBase64String(bytes)}";
        }

        if (value is IDictionary dictionary)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            var first = true;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                builder.Append(Render(entry.Key)).Append('=').Append(Render(entry.Value));
            }

            builder.Append('}');
            return builder.ToString();
        }

        if (value is IEnumerable sequence)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            var first = true;
            foreach (var item in sequence)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                builder.Append(Render(item));
            }

            builder.Append(']');
            return builder.ToString();
        }

        if (value is IConvertible convertible)
        {
            return $"{type.FullName}:{System.Convert.ToString(convertible, CultureInfo.InvariantCulture)}";
        }

        return $"{type.FullName}:{value}";
    }

    private static string CreateRevision(
        IReadOnlyDictionary<string, object?> converted,
        IReadOnlyList<TextAssignment> unmatched,
        CancellationToken cancellationToken
    )
    {
        var keys = new List<string>(converted.Keys);
        keys.Sort(StringComparer.Ordinal);
        var unmatchedKeys = new List<string>();
        foreach (var item in unmatched)
        {
            unmatchedKeys.Add(item.Origin);
        }

        unmatchedKeys.Sort(StringComparer.Ordinal);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendHashedString(hash, path);
            AppendHashedString(hash, Render(converted[path]));
        }

        foreach (var origin in unmatchedKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Include unmatched origins so transports observe underlying changes
            // even when no member is bound.
            var raw = unmatched
                .First(item => string.Equals(item.Origin, origin, StringComparison.Ordinal))
                .RawValue;
            AppendHashedString(hash, origin);
            AppendHashedString(hash, Render(raw));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendHashedString(IncrementalHash hash, string value)
    {
        Span<byte> prefix = stackalloc byte[12];
#if NETSTANDARD
        var prefixLength = WriteLengthPrefix(prefix, value.Length);
        hash.AppendData(prefix[..prefixLength].ToArray());
        hash.AppendData(Encoding.UTF8.GetBytes(value));
#else
        var prefixLength = WriteLengthPrefix(prefix, value.Length);
        hash.AppendData(prefix[..prefixLength]);
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount == 0)
        {
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(value, buffer);
            hash.AppendData(buffer.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
#endif
    }

    private static int WriteLengthPrefix(Span<byte> buffer, int length)
    {
        var digits = 0;
        var remaining = length;
        do
        {
            digits++;
            remaining /= 10;
        } while (remaining != 0);

        var index = digits;
        remaining = length;
        do
        {
            index--;
            buffer[index] = (byte)('0' + (remaining % 10));
            remaining /= 10;
        } while (remaining != 0);

        buffer[digits] = (byte)':';
        return digits + 1;
    }
}

/// <summary>
/// A case-insensitive index over one schema's members that distinguishes a unique match
/// from an ambiguous one, shared by flat key/value transports.
/// </summary>
internal sealed class TextAssignmentMemberLookup
{
    private readonly Dictionary<string, ConfiglueMemberSchema> _membersByName;
    private readonly HashSet<string> _ambiguousNames;

    private TextAssignmentMemberLookup(
        Dictionary<string, ConfiglueMemberSchema> membersByName,
        HashSet<string> ambiguousNames
    )
    {
        _membersByName = membersByName;
        _ambiguousNames = ambiguousNames;
    }

    public static TextAssignmentMemberLookup Create(ConfiglueModelSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var membersByName = new Dictionary<string, ConfiglueMemberSchema>(
            StringComparer.OrdinalIgnoreCase
        );
        var ambiguousNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in schema.Members)
        {
            if (ambiguousNames.Contains(member.Name))
            {
                continue;
            }

            if (!membersByName.TryAdd(member.Name, member))
            {
                ambiguousNames.Add(member.Name);
                membersByName.Remove(member.Name);
            }
        }

        return new TextAssignmentMemberLookup(membersByName, ambiguousNames);
    }

    /// <summary>Resolves a path segment to a unique member or reports an ambiguous segment.</summary>
    public bool TryResolve(string segment, out ConfiglueMemberSchema member, out bool isAmbiguous)
    {
        if (_ambiguousNames.Contains(segment))
        {
            member = default;
            isAmbiguous = true;
            return false;
        }

        if (_membersByName.TryGetValue(segment, out member))
        {
            isAmbiguous = false;
            return true;
        }

        member = default;
        isAmbiguous = false;
        return false;
    }
}
