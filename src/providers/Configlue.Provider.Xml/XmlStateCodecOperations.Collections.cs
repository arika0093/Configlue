using System.Collections;
using System.Linq;
using System.Xml;

namespace Configlue.Provider.Xml;

internal static partial class XmlStateCodecOperations
{
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
}
