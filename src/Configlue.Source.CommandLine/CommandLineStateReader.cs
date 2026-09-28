using System.Buffers;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Configlue;

namespace Configlue.Source.CommandLine;

/// <summary>Reads mapped command-line values into a generated sparse model fragment.</summary>
/// <typeparam name="TFragment">The generated fragment type.</typeparam>
internal sealed class CommandLineStateReader<TFragment> : IStateReader<TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly ConfiglueModelSchema _schema;
    private readonly CommandLineSourceOptions _options;
    private readonly IReadOnlyList<CommandLineMappingBuilder.Mapping> _mappings;
    private readonly object _cacheLock = new();
    private StateReadResult<TFragment> _cachedResult;
    private volatile bool _hasCachedResult;

    public CommandLineStateReader(
        ConfiglueModelSchema schema,
        CommandLineSourceOptions options,
        IReadOnlyList<CommandLineMappingBuilder.Mapping> mappings
    )
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mappings);
        _schema = schema;
        _options = options;
        _mappings = mappings;
    }

    public ValueTask<StateReadResult<TFragment>> ReadAsync(
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_hasCachedResult)
        {
            lock (_cacheLock)
            {
                if (!_hasCachedResult)
                {
                    _cachedResult = ReadCore(cancellationToken);
                    _hasCachedResult = true;
                }
            }
        }

        return ValueTask.FromResult(_cachedResult);
    }

    private StateReadResult<TFragment> ReadCore(CancellationToken cancellationToken)
    {
        var parseResult = _options.ParseResult;
        if (parseResult.Errors.Count > 0)
        {
            throw new FormatException(
                "The command-line parse result contains errors: "
                    + string.Join("; ", parseResult.Errors.Select(error => error.Message))
            );
        }

        if (parseResult.UnmatchedTokens.Count > 0)
        {
            throw new FormatException(
                "The command line contains unmatched tokens: "
                    + string.Join(" ", parseResult.UnmatchedTokens)
            );
        }

        var assignments = new Dictionary<string, AssignedValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in _mappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = mapping.Resolve(parseResult);
            if (resolved is not { } value)
            {
                continue;
            }

            // Later mappings win when several symbols target one member.
            assignments[mapping.PropertyPath] = new AssignedValue(
                mapping.PropertyPathSegments,
                value.Value,
                mapping.Symbol.Name
            );
        }

        var revision = CreateRevision(assignments, cancellationToken);
        if (assignments.Count == 0)
        {
            return StateReadResult<TFragment>.NotFound(revision);
        }

        IConfiglueFragment fragment = _schema.CreateEmptyFragment();
        foreach (var assignment in assignments.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fragment = SetValue(
                fragment,
                _schema,
                assignment.Path,
                0,
                assignment.Value,
                assignment.SymbolName
            );
        }

        return StateReadResult<TFragment>.Success(
            (TFragment)fragment,
            revision,
            _schema.ToMetadata()
        );
    }

    private static IConfiglueFragment SetValue(
        IConfiglueFragment fragment,
        ConfiglueModelSchema schema,
        string[] path,
        int pathIndex,
        object? value,
        string symbolName
    )
    {
        var members = schema.Members;
        ConfiglueMemberSchema member = default;
        var matches = 0;
        for (var index = 0; index < members.Count; index++)
        {
            var candidate = members[index];
            if (!string.Equals(candidate.Name, path[pathIndex], StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matches++;
            member = candidate;
        }

        if (matches > 1)
        {
            throw new FormatException(
                $"Command-line path segment '{path[pathIndex]}' is ambiguous in schema '{schema.Id}'."
            );
        }

        if (matches == 0)
        {
            throw new FormatException(
                $"Command-line path segment '{path[pathIndex]}' is unknown in schema '{schema.Id}'."
            );
        }

        if (pathIndex == path.Length - 1)
        {
            var converted = CommandLineValueConverter.Convert(
                value,
                member.ValueType,
                string.Join('.', path),
                symbolName
            );
            return fragment.WithMember(member.Id, converted);
        }

        if (member.NestedSchemaFactory is null)
        {
            throw new FormatException(
                $"Command-line path '{string.Join('.', path)}' continues past non-nested member '{member.Name}'."
            );
        }

        var nestedSchema = member.NestedSchemaFactory();
        var nestedFragment =
            FindPresentMember(fragment, member.Id) as IConfiglueFragment
            ?? nestedSchema.CreateEmptyFragment();
        return fragment.WithMember(
            member.Id,
            SetValue(nestedFragment, nestedSchema, path, pathIndex + 1, value, symbolName)
        );
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

    private static string CreateRevision(
        Dictionary<string, AssignedValue> assignments,
        CancellationToken cancellationToken
    )
    {
        var keys = new List<string>(assignments.Keys);
        keys.Sort(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rendered = CommandLineValueConverter.Render(assignments[path].Value);
            AppendHashedString(hash, path);
            AppendHashedString(hash, rendered);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendHashedString(IncrementalHash hash, string value)
    {
        Span<byte> prefix = stackalloc byte[12];
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

    private sealed record AssignedValue
    {
        public string[] Path { get; init; }
        public object? Value { get; init; }
        public string SymbolName { get; init; }

        public AssignedValue(string[] Path, object? Value, string SymbolName)
        {
            this.Path = Path;
            this.Value = Value;
            this.SymbolName = SymbolName;
        }

        public void Deconstruct(out string[] Path, out object? Value, out string SymbolName)
        {
            Path = this.Path;
            Value = this.Value;
            SymbolName = this.SymbolName;
        }
    }
}

/// <summary>Converts parsed command-line values to generated model member types without reflection-based serialization.</summary>
internal static class CommandLineValueConverter
{
    public static object? Convert(object? parsed, Type targetType, string path, string symbolName)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var valueType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (parsed is null)
        {
            if (valueType.IsValueType)
            {
                throw new FormatException(
                    $"The command-line value for model path '{path}' from '{symbolName}' is null but requires '{targetType}'."
                );
            }

            return null;
        }

        if (valueType.IsInstanceOfType(parsed))
        {
            return parsed;
        }

        if (valueType == typeof(string))
        {
            return parsed is IConvertible convertible
                ? System.Convert.ToString(convertible, CultureInfo.InvariantCulture)
                : parsed.ToString();
        }

        if (parsed is string text)
        {
            return ParseText(text, valueType, path, symbolName);
        }

        if (valueType.IsEnum && IsIntegral(parsed))
        {
            return Enum.ToObject(valueType, parsed);
        }

        if (
            parsed is IConvertible
            && typeof(IConvertible).IsAssignableFrom(valueType)
            && valueType != typeof(object)
        )
        {
            try
            {
                return System.Convert.ChangeType(parsed, valueType, CultureInfo.InvariantCulture);
            }
            catch (Exception exception)
                when (exception is FormatException or InvalidCastException or OverflowException)
            {
                throw ConversionFailure(path, symbolName, parsed, targetType, exception);
            }
        }

        if (parsed is IEnumerable sequence)
        {
            return ConvertSequence(sequence, valueType, path, symbolName);
        }

        throw ConversionFailure(path, symbolName, parsed, targetType, null);
    }

    public static string Render(object? value)
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

        if (value is DateOnly dateOnly)
        {
            return $"{type.FullName}:{dateOnly.ToString("O", CultureInfo.InvariantCulture)}";
        }

        if (value is TimeOnly timeOnly)
        {
            return $"{type.FullName}:{timeOnly.ToString("O", CultureInfo.InvariantCulture)}";
        }

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
            builder.Append(type.FullName).Append(":{");
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
            builder.Append(type.FullName).Append('[');
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

    private static object ParseText(string text, Type valueType, string path, string symbolName)
    {
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
                    $"The command-line value for model path '{path}' must contain exactly one character."
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

            if (valueType == typeof(DateOnly))
            {
                return DateOnly.Parse(text, CultureInfo.InvariantCulture);
            }

            if (valueType == typeof(TimeOnly))
            {
                return TimeOnly.Parse(text, CultureInfo.InvariantCulture);
            }

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
            throw ConversionFailure(path, symbolName, text, valueType, exception);
        }

        throw ConversionFailure(path, symbolName, text, valueType, null);
    }

    private static object ConvertSequence(
        IEnumerable sequence,
        Type valueType,
        string path,
        string symbolName
    )
    {
        if (valueType.IsArray && valueType.GetArrayRank() == 1)
        {
            var elementType = valueType.GetElementType()!;
            var items = sequence
                .Cast<object?>()
                .Select(item => Convert(item, elementType, path, symbolName))
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
                .Select(item => Convert(item, elementType, path, symbolName))
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
                return CreateList(elementType, items, path, symbolName, valueType);
            }

            if (definition == typeof(HashSet<>) || definition == typeof(ISet<>))
            {
                return CreateSet(elementType, items, path, symbolName, valueType);
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
                return CreateDictionary(valueType, dictionary, path, symbolName);
            }
        }

        throw ConversionFailure(path, symbolName, sequence, valueType, null);
    }

    [RequiresUnreferencedCode(
        "Concrete collection construction reflects over generic instantiations that trimming may remove."
    )]
    [RequiresDynamicCode("Concrete collection construction may require runtime code generation.")]
    private static object CreateList(
        Type elementType,
        object?[] items,
        string path,
        string symbolName,
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
            throw ConversionFailure(path, symbolName, items, valueType, exception);
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
        string symbolName,
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
            throw ConversionFailure(path, symbolName, items, valueType, exception);
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
        string symbolName
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
                var key = Convert(entry.Key, valueType.GetGenericArguments()[0], path, symbolName);
                if (key is null)
                {
                    throw ConversionFailure(path, symbolName, entry.Key, valueType, null);
                }

                created.Add(
                    key,
                    Convert(entry.Value, valueType.GetGenericArguments()[1], path, symbolName)
                );
            }

            return created;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw ConversionFailure(path, symbolName, dictionary, valueType, exception);
        }
    }

    private static bool IsIntegral(object? value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong;

    private static FormatException ConversionFailure(
        string path,
        string symbolName,
        object? parsed,
        Type targetType,
        Exception? inner
    ) =>
        new(
            $"The command-line value for model path '{path}' from '{symbolName}' cannot be converted from '{parsed?.GetType()}' to '{targetType}'. Map the symbol with a converter for custom shapes.",
            inner
        );
}
