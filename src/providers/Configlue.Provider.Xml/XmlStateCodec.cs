using System.Buffers;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace Configlue.Provider.Xml;

/// <summary>An XML codec for ordinary models and generated sparse fragments.</summary>
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
                WriteFragment(writer, fragment, schema ?? fragment.ConfiglueSchema.ToMetadata(), RootName);
            }
            else if (
                value is not null
                && TryCreateGeneratedFragment(type, value, out var generatedFragment)
            )
            {
                WriteFragment(
                    writer,
                    generatedFragment,
                    schema ?? generatedFragment.ConfiglueSchema.ToMetadata(),
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
            var member = fragment.ConfiglueSchema.Members.First(item => item.Id == present.Id);
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
            WriteFragment(writer, nestedFragment, nestedFragment.ConfiglueSchema.ToMetadata(), FragmentName);
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

            var member = fragment.ConfiglueSchema.Members.FirstOrDefault(candidate => candidate.Id == id);
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

            Type collectionType;
            if (valueType.IsInterface)
            {
                var isSet = valueType
                    .GetInterfaces()
                    .Append(valueType)
                    .Any(candidate =>
                        candidate.IsGenericType
                        && (
                            candidate.GetGenericTypeDefinition() == typeof(ISet<>)
                            || candidate.GetGenericTypeDefinition() == typeof(IReadOnlySet<>)
                        )
                    );
                collectionType = isSet
                    ? typeof(HashSet<>).MakeGenericType(elementType)
                    : typeof(List<>).MakeGenericType(elementType);
            }
            else
            {
                collectionType = valueType;
            }
            var collection = Activator.CreateInstance(collectionType);
            var add =
                collectionType.GetMethod("Add", [elementType])
                ?? throw new XmlException($"Collection type '{collectionType}' has no Add method.");
            foreach (var item in items)
            {
                add.Invoke(collection, [item]);
            }

            return collection;
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
