using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
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
/// explicit materialization contract: arrays, list and read-only-list interfaces, set interfaces,
/// dictionaries, queues, stacks, linked and sorted collections, observable and read-only
/// collections, and immutable arrays, lists, sets, and dictionaries are materialized with
/// assignable values. Other enumerable shapes fail with <see cref="XmlException"/>.
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
        foreach (var present in fragment.EnumeratePresentMembers())
        {
            var member = fragment.Schema.Members.First(item => item.Id == present.Id);
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

            var member = fragment.Schema.Members.FirstOrDefault(candidate => candidate.Id == id);
            if (member.Name is null)
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
                    var valueElement =
                        item.Element("value")?.Elements().FirstOrDefault()
                        ?? throw new XmlException("A dictionary entry has no value.");
                    var key = ReadValue(keyElement, keyType);
                    var dictionaryValue = ReadValue(valueElement, dictionaryValueType);
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
            WriteValue(writer, valueType, entryType.GetProperty("Value")!.GetValue(entry)!);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    /// <summary>
    /// XML fragment collections use the same public shapes recognized by the generated
    /// fragment clone contract. Interface declarations are materialized as lists, sets,
    /// or dictionaries; concrete supported collections use their
    /// collection-specific constructor/factory. Other enumerable types are rejected here,
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

        if (!declaredType.IsAssignableFrom(concreteType))
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
        if (definition == typeof(Stack<>))
        {
            Array.Reverse(typedItems);
        }

        object? collection = null;
        if (IsNamedGenericType(definition, "System.Collections.Immutable.ImmutableArray`1"))
        {
            collection = InvokeImmutableFactory(
                definition,
                "System.Collections.Immutable.ImmutableArray",
                "CreateRange",
                [elementType],
                enumerable
            );
        }
        else if (IsNamedGenericType(definition, "System.Collections.Immutable.ImmutableList`1"))
        {
            collection = InvokeImmutableFactory(
                definition,
                "System.Collections.Immutable.ImmutableList",
                "CreateRange",
                [elementType],
                enumerable
            );
        }
        else if (IsNamedGenericType(definition, "System.Collections.Immutable.ImmutableHashSet`1"))
        {
            collection = InvokeImmutableFactory(
                definition,
                "System.Collections.Immutable.ImmutableHashSet",
                "CreateRange",
                [elementType],
                enumerable
            );
        }
        else if (
            IsNamedGenericType(definition, "System.Collections.Immutable.ImmutableDictionary`2")
        )
        {
            collection = InvokeImmutableFactory(
                definition,
                "System.Collections.Immutable.ImmutableDictionary",
                "CreateRange",
                arguments,
                enumerable
            );
        }
        else
        {
            var enumerableConstructor = concreteType
                .GetConstructors()
                .FirstOrDefault(constructor =>
                    constructor.GetParameters() is [{ ParameterType: var parameterType }]
                    && parameterType.IsInstanceOfType(typedItems)
                );
            if (enumerableConstructor is not null)
            {
                collection = enumerableConstructor.Invoke([enumerable]);
            }
            else
            {
                collection = Activator.CreateInstance(concreteType);
                string methodName;
                if (definition == typeof(Queue<>) || definition == typeof(ConcurrentQueue<>))
                {
                    methodName = "Enqueue";
                }
                else if (definition == typeof(Stack<>) || definition == typeof(ConcurrentStack<>))
                {
                    methodName = "Push";
                }
                else if (definition == typeof(LinkedList<>))
                {
                    methodName = "AddLast";
                }
                else
                {
                    methodName = "Add";
                }
                var add = isDictionary
                    ? concreteType.GetMethod("Add", arguments)
                    : concreteType.GetMethod(methodName, [elementType]);
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
            }
        }

        if (collection is null || !declaredType.IsInstanceOfType(collection))
        {
            throw UnsupportedCollection(declaredType);
        }

        return collection;
    }

    private static bool IsDictionaryType(Type type) =>
        type == typeof(Dictionary<,>)
        || type == typeof(IDictionary<,>)
        || type == typeof(IReadOnlyDictionary<,>)
        || type == typeof(SortedDictionary<,>)
        || type == typeof(SortedList<,>)
        || IsNamedGenericType(type, "System.Collections.Immutable.ImmutableDictionary`2");

    private static bool IsSetType(Type type) =>
        type == typeof(HashSet<>)
        || type == typeof(ISet<>)
        || IsNamedGenericType(type, "System.Collections.Generic.IReadOnlySet`1");

    private static bool IsSupportedConcreteCollection(Type type, bool isDictionary) =>
        (
            isDictionary
            && (
                type == typeof(Dictionary<,>)
                || type == typeof(SortedDictionary<,>)
                || type == typeof(SortedList<,>)
                || IsNamedGenericType(type, "System.Collections.Immutable.ImmutableDictionary`2")
            )
        )
        || type == typeof(List<>)
        || type == typeof(HashSet<>)
        || type == typeof(Queue<>)
        || type == typeof(Stack<>)
        || type == typeof(ConcurrentQueue<>)
        || type == typeof(ConcurrentStack<>)
        || type == typeof(BlockingCollection<>)
        || type == typeof(LinkedList<>)
        || type == typeof(SortedSet<>)
        || type == typeof(ObservableCollection<>)
        || type == typeof(ReadOnlyCollection<>)
        || IsNamedGenericType(type, "System.Collections.Immutable.ImmutableArray`1")
        || IsNamedGenericType(type, "System.Collections.Immutable.ImmutableList`1")
        || IsNamedGenericType(type, "System.Collections.Immutable.ImmutableHashSet`1");

    private static XmlException UnsupportedCollection(Type type) =>
        new($"XML collection materialization does not support declared collection type '{type}'.");

    private static object? InvokeImmutableFactory(
        Type collectionType,
        string factoryTypeName,
        string methodName,
        Type[] arguments,
        object values
    )
    {
        var factoryType =
            collectionType.Assembly.GetType(factoryTypeName)
            ?? throw new InvalidOperationException(
                $"Immutable collection factory '{factoryTypeName}' is unavailable."
            );
        var factory = factoryType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method =>
                method.Name == methodName
                && method.IsGenericMethodDefinition
                && method.GetGenericArguments().Length == arguments.Length
                && method.GetParameters() is [{ ParameterType: var parameterType }]
                && parameterType.IsGenericType
                && parameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            );
        if (factory is null)
        {
            throw new InvalidOperationException(
                $"Immutable collection factory '{factoryType}.{methodName}' is unavailable."
            );
        }
        return factory.MakeGenericMethod(arguments).Invoke(null, [values]);
    }

    private static bool IsNamedGenericType(Type type, string name) =>
        type.IsGenericTypeDefinition && type.FullName == name;

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
