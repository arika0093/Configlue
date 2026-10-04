using System.Buffers;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using Configlue.CompilerServices;

namespace Configlue.Provider.Xml;

/// <summary>
/// An XML codec for ordinary models and generated sparse fragments. Sequence members use an
/// explicit materialization contract (issue #280): arrays, <c>List{T}</c> and
/// read-only-list interfaces, <c>HashSet{T}</c>/set interfaces, and dictionaries
/// (<c>Dictionary{TKey, TValue}</c>, <c>SortedDictionary{TKey, TValue}</c>,
/// <c>SortedList{TKey, TValue}</c> and their read-only interfaces) are materialized
/// with assignable values. Queues, stacks, concurrent collections,
/// <c>BlockingCollection{T}</c>, <c>PriorityQueue{TElement, TPriority}</c>,
/// <c>LinkedList{T}</c>, <c>SortedSet{T}</c>, observable/read-only wrappers and
/// immutable collections are unsupported here and fail with <see cref="XmlException"/>;
/// use a first-class shape or a custom policy instead.
/// On <c>netstandard2.0</c>, an <c>IReadOnlySet{T}</c> contract has no BCL identity
/// and no runtime proxy is emitted, so it is likewise unsupported.
/// </summary>
public sealed class XmlStateCodec
    : IStateCodec,
        IStateSchemaMetadataReader,
        IStateCodecRecoveryPolicy
{
    /// <inheritdoc />
    [RequiresUnreferencedCode("XmlSerializer requires reflected model metadata.")]
    [RequiresDynamicCode("XmlSerializer may generate code at runtime.")]
    public object? Deserialize(
        Type type,
        in ReadOnlySequence<byte> source,
        in StateCodecContext context
    )
    {
        ArgumentNullException.ThrowIfNull(type);
        return XmlStateCodecOperations.Deserialize(type, source.ToArray());
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode("XmlSerializer requires reflected model metadata.")]
    [RequiresDynamicCode("XmlSerializer may generate code at runtime.")]
    public void Serialize(
        Type type,
        object? value,
        IBufferWriter<byte> destination,
        in StateCodecContext context
    )
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(destination);
        var bytes = XmlStateCodecOperations.Serialize(type, value, context.Schema);
        bytes.CopyTo(destination.GetSpan(bytes.Length));
        destination.Advance(bytes.Length);
    }

    /// <inheritdoc />
    public StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source) =>
        XmlStateCodecOperations.ReadSchemaMetadata(source.ToArray());

    /// <inheritdoc />
    public bool IsRecoverableReadException(Exception exception) =>
        exception is XmlException
        || exception is InvalidOperationException { InnerException: XmlException };
}

/// <summary>A typed XML codec fast path.</summary>
public sealed class XmlStateCodec<T>
    : IStateCodec<T>,
        IStateSchemaMetadataReader,
        IStateCodecRecoveryPolicy
{
    /// <inheritdoc />
    [RequiresUnreferencedCode("XmlSerializer requires reflected model metadata.")]
    [RequiresDynamicCode("XmlSerializer may generate code at runtime.")]
    public T? Deserialize(in ReadOnlySequence<byte> source, in StateCodecContext context) =>
        (T?)XmlStateCodecOperations.Deserialize(typeof(T), source.ToArray());

    /// <inheritdoc />
    [RequiresUnreferencedCode("XmlSerializer requires reflected model metadata.")]
    [RequiresDynamicCode("XmlSerializer may generate code at runtime.")]
    public void Serialize(T? value, IBufferWriter<byte> destination, in StateCodecContext context)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var bytes = XmlStateCodecOperations.Serialize(typeof(T), value, context.Schema);
        bytes.CopyTo(destination.GetSpan(bytes.Length));
        destination.Advance(bytes.Length);
    }

    /// <inheritdoc />
    public StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source) =>
        XmlStateCodecOperations.ReadSchemaMetadata(source.ToArray());

    /// <inheritdoc />
    public bool IsRecoverableReadException(Exception exception) =>
        exception is XmlException
        || exception is InvalidOperationException { InnerException: XmlException };
}

internal static class XmlStateCodecOperations
{
    private const string RootName = "configlue";
    private const string FragmentName = "fragment";
    private const string MemberName = "member";
    private const string SequenceName = "sequence";
    private const string ItemName = "item";
    private const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly ConditionalWeakTable<Type, XmlSerializer> Serializers = new();

    [RequiresUnreferencedCode("XmlSerializer requires reflected model metadata.")]
    [RequiresDynamicCode("XmlSerializer may generate code at runtime.")]
    public static byte[] Serialize(Type type, object? value, StateSchemaMetadata? schema)
    {
        using var output = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            OmitXmlDeclaration = false,
            CloseOutput = false,
        };
        using (var writer = XmlWriter.Create(output, settings))
        {
            writer.WriteStartDocument();
            if (value is IConfiglueFragment fragment)
            {
                WriteFragment(writer, fragment, schema ?? fragment.Schema.ToMetadata(), RootName);
            }
            else if (
                value is not null
                && TryCreateGeneratedFragment(type, value, out var generatedFragment)
            )
            {
                WriteFragment(
                    writer,
                    generatedFragment,
                    schema ?? generatedFragment.Schema.ToMetadata(),
                    RootName
                );
            }
            else if (schema is { } metadata)
            {
                writer.WriteStartElement(RootName);
                WriteSchemaAttributes(writer, metadata);
                writer.WriteStartElement("value");
                GetSerializer(type).Serialize(writer, value);
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            else
            {
                GetSerializer(type).Serialize(writer, value);
            }

            writer.WriteEndDocument();
        }

        return output.ToArray();
    }

    [RequiresUnreferencedCode("XmlSerializer requires reflected model metadata.")]
    [RequiresDynamicCode("XmlSerializer may generate code at runtime.")]
    public static object? Deserialize(Type type, byte[] content)
    {
        var document = LoadDocument(content);
        var root = document.Root ?? throw new XmlException("The XML document has no root element.");
        if (typeof(IConfiglueFragment).IsAssignableFrom(type))
        {
            return ReadFragment(root, type);
        }

        if (
            root.Name.LocalName == RootName
            && TryGetGeneratedFragmentType(type) is { } generatedFragmentType
        )
        {
            var fragment = ReadFragment(root, generatedFragmentType);
            return generatedFragmentType
                .GetMethod("ToModel", Type.EmptyTypes)!
                .Invoke(fragment, null);
        }

        var valueElement = root.Name.LocalName == RootName ? root.Element("value") : root;
        if (valueElement is null)
        {
            throw new XmlException("The Configlue XML envelope is missing its value element.");
        }

        return GetSerializer(type).Deserialize(valueElement.CreateReader());
    }

    public static StateSchemaMetadata? ReadSchemaMetadata(byte[] content)
    {
        var root = LoadDocument(content).Root;
        if (
            root is null
            || root.Name.LocalName != RootName
            || !int.TryParse(
                (string?)root.Attribute("version"),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var version
            )
            || version < StateSchemaMetadata.InitialVersion
        )
        {
            return null;
        }

        return new StateSchemaMetadata((string?)root.Attribute("id"), version);
    }

    [RequiresUnreferencedCode("XML fragment models are inspected through reflection.")]
    [RequiresDynamicCode("XmlSerializer may generate code at runtime.")]
    private static void WriteFragment(
        XmlWriter writer,
        IConfiglueFragment fragment,
        StateSchemaMetadata schema,
        string elementName
    )
    {
        writer.WriteStartElement(elementName);
        WriteSchemaAttributes(writer, schema);
        var memberSchema = fragment.Schema;
        foreach (var present in fragment.EnumeratePresentMembers())
        {
            var member = memberSchema.GetMember(present.Id);
            writer.WriteStartElement(MemberName);
            writer.WriteAttributeString(
                "id",
                present.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            );
            writer.WriteAttributeString("name", present.Name);
            if (present.Value is null)
            {
                writer.WriteAttributeString("xsi", "nil", XsiNamespace, "true");
            }
            else
            {
                WriteValue(writer, member.ValueType, present.Value);
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    [RequiresUnreferencedCode("XML value models are inspected through reflection.")]
    [RequiresDynamicCode("XmlSerializer may generate code at runtime.")]
    private static void WriteValue(XmlWriter writer, Type valueType, object value)
    {
        if (value is IConfiglueFragment nestedFragment)
        {
            WriteFragment(writer, nestedFragment, nestedFragment.Schema.ToMetadata(), FragmentName);
            return;
        }

        if (TryGetDictionaryTypes(valueType, out var keyType, out var dictionaryValueType))
        {
            WriteDictionary(writer, value, keyType, dictionaryValueType);
            return;
        }

        if (TryGetEnumerableElementType(valueType, out var elementType))
        {
            writer.WriteStartElement(SequenceName);
            foreach (var item in (IEnumerable)value)
            {
                writer.WriteStartElement(ItemName);
                if (item is null)
                {
                    writer.WriteAttributeString("xsi", "nil", XsiNamespace, "true");
                }
                else
                {
                    WriteValue(writer, elementType, item);
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            return;
        }

        GetSerializer(valueType).Serialize(writer, value);
    }

    [RequiresUnreferencedCode("XML fragment models are inspected through reflection.")]
    [RequiresDynamicCode("XmlSerializer may generate code at runtime.")]
    private static IConfiglueFragment ReadFragment(XElement element, Type fragmentType)
    {
        var emptyProperty =
            fragmentType.GetProperty("Empty", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                $"Generated fragment '{fragmentType}' has no Empty value."
            );
        var fragment =
            (IConfiglueFragment?)emptyProperty.GetValue(null)
            ?? throw new InvalidOperationException(
                $"Generated fragment '{fragmentType}' returned a null Empty value."
            );
        var seenIds = new HashSet<int>();
        var memberSchema = fragment.Schema;

        foreach (var memberElement in element.Elements(MemberName))
        {
            var idText = (string?)memberElement.Attribute("id");
            if (
                !int.TryParse(
                    idText,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var id
                )
            )
            {
                throw new XmlException("A Configlue XML member must have a numeric id.");
            }

            if (!seenIds.Add(id))
            {
                throw new XmlException($"Duplicate Configlue XML member id '{id}'.");
            }

            if (!memberSchema.TryGetMember(id, out var member))
            {
                continue;
            }

            object? value;
            if (IsNil(memberElement))
            {
                value = null;
            }
            else
            {
                var valueElement =
                    memberElement.Elements().FirstOrDefault()
                    ?? throw new XmlException($"Member '{member.Name}' has no value element.");
                value = ReadValue(valueElement, member.ValueType);
            }

            if (
                value is null
                && member.ValueType.IsValueType
                && Nullable.GetUnderlyingType(member.ValueType) is null
            )
            {
                throw new XmlException($"Non-nullable member '{member.Name}' cannot be null.");
            }

            fragment = fragment.WithMember(id, value);
        }

        return fragment;
    }

    [RequiresUnreferencedCode("XML value models are inspected through reflection.")]
    [RequiresDynamicCode("XmlSerializer may generate code at runtime.")]
    private static object? ReadValue(XElement element, Type valueType)
    {
        if (IsNil(element))
        {
            return null;
        }

        if (typeof(IConfiglueFragment).IsAssignableFrom(valueType))
        {
            return ReadFragment(element, valueType);
        }

        if (
            TryGetDictionaryTypes(valueType, out var keyType, out var dictionaryValueType)
            && element.Name.LocalName == SequenceName
        )
        {
            var pairType = typeof(KeyValuePair<,>).MakeGenericType(keyType, dictionaryValueType);
            var entries = element
                .Elements(ItemName)
                .Select(item =>
                {
                    var keyElement =
                        item.Element("key")?.Elements().FirstOrDefault()
                        ?? throw new XmlException("A dictionary entry has no key value.");
                    var valueWrapper =
                        item.Element("value")
                        ?? throw new XmlException("A dictionary entry has no value.");
                    object? dictionaryValue;
                    if (IsNil(valueWrapper))
                    {
                        if (
                            dictionaryValueType.IsValueType
                            && Nullable.GetUnderlyingType(dictionaryValueType) is null
                        )
                        {
                            throw new XmlException("Non-nullable dictionary value cannot be null.");
                        }

                        dictionaryValue = null;
                    }
                    else
                    {
                        var valueElement =
                            valueWrapper.Elements().FirstOrDefault()
                            ?? throw new XmlException("A dictionary entry has no value.");
                        dictionaryValue = ReadValue(valueElement, dictionaryValueType);
                        if (
                            dictionaryValue is null
                            && dictionaryValueType.IsValueType
                            && Nullable.GetUnderlyingType(dictionaryValueType) is null
                        )
                        {
                            throw new XmlException("Non-nullable dictionary value cannot be null.");
                        }
                    }

                    var key = ReadValue(keyElement, keyType);
                    return Activator.CreateInstance(pairType, [key, dictionaryValue]);
                })
                .ToArray();
            return MaterializeCollection(valueType, pairType, entries);
        }

        if (
            TryGetEnumerableElementType(valueType, out var elementType)
            && element.Name.LocalName == SequenceName
        )
        {
            var items = element
                .Elements(ItemName)
                .Select(item =>
                {
                    if (IsNil(item))
                    {
                        return null;
                    }

                    var inner = item.Elements().FirstOrDefault();
                    return inner is null ? null : ReadValue(inner, elementType);
                })
                .ToArray();
            if (valueType.IsArray)
            {
                var array = Array.CreateInstance(elementType, items.Length);
                for (var index = 0; index < items.Length; index++)
                {
                    array.SetValue(items[index], index);
                }

                return array;
            }

            return MaterializeCollection(valueType, elementType, items);
        }

        return GetSerializer(valueType).Deserialize(element.CreateReader());
    }

    private static void WriteSchemaAttributes(XmlWriter writer, StateSchemaMetadata schema)
    {
        if (schema.ModelId is not null)
        {
            writer.WriteAttributeString("id", schema.ModelId);
        }

        writer.WriteAttributeString(
            "version",
            schema.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
    }

    private static bool IsNil(XElement element) =>
        string.Equals(
            (string?)element.Attribute(XName.Get("nil", XsiNamespace)),
            "true",
            StringComparison.OrdinalIgnoreCase
        );

    private static bool TryGetEnumerableElementType(Type type, out Type elementType)
    {
        if (type == typeof(string) || type == typeof(byte[]))
        {
            elementType = null!;
            return false;
        }

        var enumerable = type.IsArray
            ? null
            : type.GetInterfaces()
                .Append(type)
                .FirstOrDefault(candidate =>
                    candidate.IsGenericType
                    && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                );
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        if (enumerable is not null && !typeof(IDictionary).IsAssignableFrom(type))
        {
            elementType = enumerable.GetGenericArguments()[0];
            return true;
        }

        elementType = null!;
        return false;
    }

    private static bool TryGetDictionaryTypes(Type type, out Type keyType, out Type valueType)
    {
        var dictionary = type.GetInterfaces()
            .Append(type)
            .FirstOrDefault(candidate =>
                candidate.IsGenericType
                && (
                    candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                    || candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)
                )
            );
        if (dictionary is null)
        {
            keyType = null!;
            valueType = null!;
            return false;
        }
        var arguments = dictionary.GetGenericArguments();
        keyType = arguments[0];
        valueType = arguments[1];
        return true;
    }

    private static void WriteDictionary(
        XmlWriter writer,
        object value,
        Type keyType,
        Type valueType
    )
    {
        writer.WriteStartElement(SequenceName);
        foreach (var entry in (IEnumerable)value)
        {
            var entryType = entry.GetType();
            writer.WriteStartElement(ItemName);
            writer.WriteStartElement("key");
            WriteValue(writer, keyType, entryType.GetProperty("Key")!.GetValue(entry)!);
            writer.WriteEndElement();
            writer.WriteStartElement("value");
            var entryValue = entryType.GetProperty("Value")!.GetValue(entry);
            if (entryValue is null)
            {
                writer.WriteAttributeString("xsi", "nil", XsiNamespace, "true");
            }
            else
            {
                WriteValue(writer, valueType, entryValue);
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    /// <summary>
    /// XML fragment collections use the same public shapes recognized by the generated
    /// fragment clone contract (issue #280). Interface declarations are materialized as
    /// lists, sets, or dictionaries; concrete supported collections use their
    /// collection-specific constructor. Other enumerable types are rejected here,
    /// before a generated fragment can fail with a cast error.
    /// </summary>
    private static object MaterializeCollection(
        Type declaredType,
        Type elementType,
        object?[] items
    )
    {
        var definition = declaredType.IsGenericType
            ? declaredType.GetGenericTypeDefinition()
            : declaredType;
        var arguments = declaredType.IsGenericType
            ? declaredType.GetGenericArguments()
            : Type.EmptyTypes;
        var isDictionary = arguments.Length == 2 && IsDictionaryType(definition);
        var isSet = IsSetType(definition);
        var pairType = isDictionary ? typeof(KeyValuePair<,>).MakeGenericType(arguments) : null;

        Type concreteType;
        if (declaredType.IsInterface || declaredType.IsAbstract)
        {
            if (isDictionary)
            {
                concreteType = typeof(Dictionary<,>).MakeGenericType(arguments);
            }
            else if (isSet)
            {
                concreteType = typeof(HashSet<>).MakeGenericType(elementType);
            }
            else if (
                definition == typeof(IEnumerable<>)
                || definition == typeof(ICollection<>)
                || definition == typeof(IReadOnlyCollection<>)
                || definition == typeof(IList<>)
                || definition == typeof(IReadOnlyList<>)
            )
            {
                concreteType = typeof(List<>).MakeGenericType(elementType);
            }
            else
            {
                throw UnsupportedCollection(declaredType);
            }
        }
        else
        {
            concreteType = declaredType;
            if (!IsSupportedConcreteCollection(definition, isDictionary))
            {
                throw UnsupportedCollection(declaredType);
            }
        }

        bool assignable;
#if !NETSTANDARD
        assignable =
            declaredType.IsAssignableFrom(concreteType) || definition == typeof(IReadOnlySet<>);
#else
        assignable = declaredType.IsAssignableFrom(concreteType);
#endif
        if (!assignable)
        {
            throw UnsupportedCollection(declaredType);
        }

        var typedItems = Array.CreateInstance(elementType, items.Length);
        for (var index = 0; index < items.Length; index++)
        {
            try
            {
                typedItems.SetValue(items[index], index);
            }
            catch (Exception exception)
                when (exception is InvalidCastException or ArgumentException)
            {
                throw new XmlException(
                    $"XML item at index {index} cannot be assigned to collection '{declaredType}'.",
                    exception
                );
            }
        }

        var enumerable = typedItems;

        object? collection = MaterializeIntoConcrete(
            concreteType,
            declaredType,
            elementType,
            arguments,
            isDictionary,
            pairType,
            typedItems,
            enumerable
        );

        if (collection is null || !declaredType.IsInstanceOfType(collection))
        {
#if !NETSTANDARD
            if (definition == typeof(IReadOnlySet<>) && collection is IEnumerable sequence)
            {
                var viewType = typeof(ReadOnlySetView<>).MakeGenericType(elementType);
                var view = Activator.CreateInstance(viewType, [sequence]);
                if (view is not null && declaredType.IsInstanceOfType(view))
                {
                    return view;
                }
            }
#endif
            throw UnsupportedCollection(declaredType);
        }

        return collection;
    }

    private static object? MaterializeIntoConcrete(
        Type concreteType,
        Type declaredType,
        Type elementType,
        Type[] arguments,
        bool isDictionary,
        Type? pairType,
        Array typedItems,
        Array enumerable
    )
    {
        var enumerableConstructor = concreteType
            .GetConstructors()
            .FirstOrDefault(constructor =>
                constructor.GetParameters() is [{ ParameterType: var parameterType }]
                && parameterType.IsInstanceOfType(typedItems)
            );
        if (enumerableConstructor is not null)
        {
            return enumerableConstructor.Invoke([enumerable]);
        }

        var collection = Activator.CreateInstance(concreteType);
        var add = isDictionary
            ? concreteType.GetMethod("Add", arguments)
            : concreteType.GetMethod("Add", [elementType]);
        if (collection is null || add is null)
        {
            throw UnsupportedCollection(declaredType);
        }
        foreach (var item in typedItems)
        {
            if (isDictionary)
            {
                add.Invoke(
                    collection,
                    [
                        pairType!.GetProperty("Key")!.GetValue(item),
                        pairType.GetProperty("Value")!.GetValue(item),
                    ]
                );
            }
            else
            {
                add.Invoke(collection, [item]);
            }
        }
        return collection;
    }

    private static bool IsDictionaryType(Type type) =>
        type == typeof(Dictionary<,>)
        || type == typeof(IDictionary<,>)
        || type == typeof(IReadOnlyDictionary<,>)
        || type == typeof(SortedDictionary<,>)
        || type == typeof(SortedList<,>);

    private static bool IsSetType(Type type) =>
        type == typeof(HashSet<>)
        || type == typeof(ISet<>)
#if NETSTANDARD
        || (
            type.IsGenericTypeDefinition
            && type.FullName == "System.Collections.Generic.IReadOnlySet`1"
        );
#else
        || type == typeof(IReadOnlySet<>);
#endif

    private static bool IsSupportedConcreteCollection(Type type, bool isDictionary) =>
        (
            isDictionary
            && (
                type == typeof(Dictionary<,>)
                || type == typeof(SortedDictionary<,>)
                || type == typeof(SortedList<,>)
            )
        )
        || type == typeof(List<>)
        || type == typeof(HashSet<>);

    private static XmlException UnsupportedCollection(Type type) =>
        new(
            $"XML collection materialization does not support declared collection type '{type}'. "
                + "Supported shapes are arrays, List<T>/IList<T>/IReadOnlyList<T>/IEnumerable<T>, "
                + "HashSet<T>/ISet<T> (IReadOnlySet<T> on modern TFMs), and Dictionary<TKey, TValue> "
                + "variants (including SortedDictionary/SortedList and read-only interfaces)."
        );

#if !NETSTANDARD
    private sealed class ReadOnlySetView<T>(IEnumerable<T> values) : IReadOnlySet<T>
    {
        private readonly HashSet<T> _values = new(values);
        public int Count => _values.Count;

        public bool Contains(T item) => _values.Contains(item);

        public bool IsProperSubsetOf(IEnumerable<T> other) => _values.IsProperSubsetOf(other);

        public bool IsProperSupersetOf(IEnumerable<T> other) => _values.IsProperSupersetOf(other);

        public bool IsSubsetOf(IEnumerable<T> other) => _values.IsSubsetOf(other);

        public bool IsSupersetOf(IEnumerable<T> other) => _values.IsSupersetOf(other);

        public bool Overlaps(IEnumerable<T> other) => _values.Overlaps(other);

        public bool SetEquals(IEnumerable<T> other) => _values.SetEquals(other);

        public IEnumerator<T> GetEnumerator() => _values.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
#endif

    private static XDocument LoadDocument(byte[] content)
    {
        using var memory = new MemoryStream(content, writable: false);
        using var reader = XmlReader.Create(
            memory,
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }
        );
        return XDocument.Load(reader, LoadOptions.None);
    }

    [RequiresUnreferencedCode("XmlSerializer requires reflected model metadata.")]
    [RequiresDynamicCode("XmlSerializer may generate code at runtime.")]
    private static XmlSerializer GetSerializer(Type type) =>
        Serializers.GetValue(type, static type => new XmlSerializer(type));

    [RequiresUnreferencedCode("Generated fragment factories are resolved through reflection.")]
    [RequiresDynamicCode("Generated fragment factories may construct runtime types.")]
    private static bool TryCreateGeneratedFragment(
        Type modelType,
        object value,
        out IConfiglueFragment fragment
    )
    {
        var fragmentType = TryGetGeneratedFragmentType(modelType);
        if (fragmentType is not null)
        {
            var fromModel = fragmentType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method =>
                    method.Name == "From"
                    && method.GetParameters() is [{ ParameterType: var parameterType }]
                    && parameterType == modelType
                );
            if (fromModel?.Invoke(null, [value]) is IConfiglueFragment generated)
            {
                fragment = generated;
                return true;
            }
        }

        fragment = null!;
        return false;
    }

    [RequiresUnreferencedCode("Generated fragment types are resolved through reflection.")]
    [RequiresDynamicCode("Generated fragment types may require runtime code.")]
    private static Type? TryGetGeneratedFragmentType(Type modelType)
    {
        var fragmentType = modelType.GetNestedType("Fragment", BindingFlags.Public);
        return
            fragmentType is not null
            && typeof(IConfiglueFragment).IsAssignableFrom(fragmentType)
            && fragmentType.GetMethod("ToModel", Type.EmptyTypes) is not null
            ? fragmentType
            : null;
    }
}
