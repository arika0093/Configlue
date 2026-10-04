using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Configlue.CompilerServices;

namespace Configlue.Provider.Json;

internal sealed class JsoncDocumentEditor
{
    private readonly JsoncSyntaxTree _document;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly JsonSchemaShape? _schemaShape;
    private readonly List<TextEdit> _edits = [];

    private JsoncDocumentEditor(
        JsoncSyntaxTree document,
        JsonSerializerOptions serializerOptions,
        JsonSchemaShape? schemaShape = null
    )
    {
        _document = document;
        _serializerOptions = serializerOptions;
        _schemaShape = schemaShape;
    }

    private byte[] Source => _document.Source;

    internal static byte[] Update(
        ReadOnlyMemory<byte> current,
        ReadOnlyMemory<byte> updated,
        IReadOnlyList<string> path,
        ReadOnlyMemory<byte> schemaShape,
        JsonSerializerOptions serializerOptions
    )
    {
        return Update(current, updated, path, schemaShape, null, serializerOptions);
    }

    internal static byte[] Update(
        ReadOnlyMemory<byte> current,
        ReadOnlyMemory<byte> updated,
        IReadOnlyList<string> path,
        ReadOnlyMemory<byte> legacySchemaShape,
        JsonSchemaShape? schemaShape,
        JsonSerializerOptions serializerOptions
    )
    {
        byte[] sourceBytes;
        if (current.IsEmpty)
        {
            sourceBytes = serializerOptions.WriteIndented ? "{\n}"u8.ToArray() : "{}"u8.ToArray();
        }
        else
        {
            sourceBytes = current.ToArray();
        }

        var editor = new JsoncDocumentEditor(
            JsoncSyntaxTree.Parse(sourceBytes),
            serializerOptions,
            schemaShape
        );
        var updatedEditor = JsoncSyntaxTree.Parse(updated.ToArray());
        JsoncValueNode? shapeRoot = null;
        ConfiglueModelSchema? shapeSchema = null;
        if (schemaShape is not null)
        {
            shapeRoot = schemaShape.RootNode;
            shapeSchema = schemaShape.RootSchema;
        }
        else if (!legacySchemaShape.IsEmpty)
        {
            shapeRoot = JsoncSyntaxTree.Parse(legacySchemaShape.ToArray()).Root;
        }

        if (path.Count == 0)
        {
            editor.AddDiff(
                editor._document.Root,
                updatedEditor.Root,
                shapeRoot,
                updatedEditor.Source,
                shapeSchema
            );
        }
        else
        {
            var currentSection = editor._document.GetPath(path);
            if (currentSection is not null)
            {
                editor.AddDiff(
                    currentSection,
                    updatedEditor.Root,
                    shapeRoot,
                    updatedEditor.Source,
                    shapeSchema
                );
            }
            else
            {
                var parentLength = path.Count - 1;
                JsoncValueNode? parent = null;
                while (parentLength >= 0)
                {
                    parent = editor._document.GetPath(path, parentLength);
                    if (parent is not null)
                    {
                        break;
                    }

                    parentLength--;
                }

                if (parent is null || parent.Kind != JsonValueKind.Object)
                {
                    throw new JsonException(
                        $"JSON section path '{string.Join(":", path)}' has no containing object."
                    );
                }

                var wrapped = updatedEditor.GetRawText(updatedEditor.Root).ToArray();
                for (var index = path.Count - 1; index > parentLength; index--)
                {
                    wrapped = WrapProperty(path[index], wrapped, serializerOptions);
                }

                editor.AddPropertyAtPath(parent, path[parentLength], wrapped);
            }
        }

        return editor.ApplyEdits();
    }

    internal static byte[] CreateSchemaShape<TFragment>(
        ConfiglueModelSchema schema,
        JsonSerializerOptions? serializerOptions,
        DocumentLayoutOptions? layout,
        string? schemaReferenceBaseUri
    )
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        var fragment = CreateShallowPresentFragment(schema);
        if (fragment is not TFragment typedFragment)
        {
            throw new InvalidOperationException(
                $"The schema '{schema.Id}' created a fragment of an unexpected type."
            );
        }

        var codec = new JsonStateCodec<TFragment>(
            serializerOptions,
            ConfiglueJsonFragmentRegistry<TFragment>.Converter,
            layout
        );
        var buffer = new ArrayBufferWriter<byte>();
        codec.Serialize(
            typedFragment,
            buffer,
            new StateCodecContext(schema.ToMetadata(), null, schemaReferenceBaseUri)
        );
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The generated JSON schema shape must be an object.");
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] WrapProperty(
        string name,
        byte[] value,
        JsonSerializerOptions serializerOptions
    )
    {
        using var stream = new MemoryStream();
        using (
            var writer = new Utf8JsonWriter(
                stream,
                new JsonWriterOptions
                {
                    Indented = serializerOptions.WriteIndented,
                    IndentCharacter = serializerOptions.IndentCharacter,
                    IndentSize = serializerOptions.IndentSize,
                }
            )
        )
        {
            writer.WriteStartObject();
            writer.WritePropertyName(name);
            using var document = JsonDocument.Parse(value, JsoncSyntaxTree.DocumentOptions);
            document.RootElement.WriteTo(writer);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static IConfiglueFragment CreateShallowPresentFragment(ConfiglueModelSchema schema)
    {
        var fragment = schema.CreateEmptyFragment();
        foreach (var member in schema.Members)
        {
            // One level only: nested members stay null so recursive schemas never
            // expand into an infinite present-fragment tree. The codec serializes
            // the null as a present null property, which still yields the correct
            // wire name for this level. Deeper levels are resolved lazily from the
            // actual (finite) document via JsonSchemaShape.
            object? value = member.NestedSchemaFactory is not null
                ? null
                : member.DefaultValueFactory?.Invoke();

            fragment = fragment.WithMember(member.Id, value);
        }

        return fragment;
    }

    private void AddPropertyAtPath(JsoncValueNode parent, string name, byte[] value)
    {
        if (parent.Kind != JsonValueKind.Object)
        {
            throw new JsonException("A JSON section's containing value must be an object.");
        }

        var nameBuffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(nameBuffer))
        {
            writer.WriteStringValue(name);
        }
        var propertyName = nameBuffer.WrittenSpan;
        var property = new byte[propertyName.Length + 1 + value.Length];
        propertyName.CopyTo(property);
        property[propertyName.Length] = (byte)':';
        value.CopyTo(property, propertyName.Length + 1);
        var existingProperties = parent.Properties!;
        RewriteSeparators(parent, existingProperties, hasAdditions: true);
        AddProperties(parent, [new AddedProperty(property)], existingProperties);
    }

    private void AddDiff(
        JsoncValueNode current,
        JsoncValueNode updated,
        JsoncValueNode? shape,
        byte[] updatedSource,
        ConfiglueModelSchema? currentSchema = null
    )
    {
        if (current.Kind != updated.Kind)
        {
            AddReplacement(current, updated, updatedSource);
            return;
        }

        switch (current.Kind)
        {
            case JsonValueKind.Object:
                AddObjectDiff(current, updated, shape, updatedSource, currentSchema);
                break;
            case JsonValueKind.Array:
                AddArrayDiff(current, updated, updatedSource, currentSchema);
                break;
            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                if (!Equivalent(current, updated, updatedSource))
                {
                    AddReplacement(current, updated, updatedSource);
                }

                break;
            default:
                throw new JsonException("The JSON document contains an unsupported value kind.");
        }
    }

    private void AddObjectDiff(
        JsoncValueNode current,
        JsoncValueNode updated,
        JsoncValueNode? shape,
        byte[] updatedSource,
        ConfiglueModelSchema? currentSchema = null
    )
    {
        var currentProperties = current.Properties!;
        var updatedProperties = updated.Properties!;
        Dictionary<string, JsoncPropertyNode>? shapeByName = null;
        if (shape?.Properties is { } shapeProperties)
        {
            shapeByName = new Dictionary<string, JsoncPropertyNode>(
                shapeProperties.Count,
                StringComparer.Ordinal
            );
            for (var index = 0; index < shapeProperties.Count; index++)
            {
                shapeByName[shapeProperties[index].Name] = shapeProperties[index];
            }
        }

        var updatedByName = updatedProperties.ToDictionary(
            static property => property.Name,
            StringComparer.Ordinal
        );
        var retainedProperties = new List<JsoncPropertyNode>(currentProperties.Count);

        for (var index = 0; index < currentProperties.Count; index++)
        {
            var property = currentProperties[index];
            if (!updatedByName.TryGetValue(property.Name, out var updatedProperty))
            {
                bool isOwned;
                if (_schemaShape is null)
                {
                    isOwned = shapeByName is null || shapeByName.ContainsKey(property.Name);
                }
                else
                {
                    // Schema-aware mode: only owned members recorded in the (possibly
                    // lazily resolved) shape may be removed. Unknown members at any
                    // depth are retained. A null shape means an unknown subtree.
                    isOwned = shapeByName is not null && shapeByName.ContainsKey(property.Name);
                }

                if (isOwned)
                {
                    AddPropertyRemoval(property);
                }
                else
                {
                    retainedProperties.Add(property);
                }

                continue;
            }

            retainedProperties.Add(property);
            JsoncValueNode? shapeValue = null;
            if (
                shapeByName is not null
                && shapeByName.TryGetValue(property.Name, out var shapeProperty)
            )
            {
                shapeValue = shapeProperty.Value;
            }

            var (childShape, childSchema) = ResolveChildShape(
                property.Name,
                shapeValue,
                currentSchema
            );
            AddDiff(property.Value, updatedProperty.Value, childShape, updatedSource, childSchema);
        }

        var currentNames = currentProperties
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var additions = updatedProperties
            .Where(property => !currentNames.Contains(property.Name))
            .Select(property => new AddedProperty(
                updatedSource
                    .AsSpan(property.NameStart, property.Value.End - property.NameStart)
                    .ToArray()
            ))
            .ToArray();
        RewriteSeparators(current, retainedProperties, additions.Length > 0);
        if (additions.Length > 0)
        {
            AddProperties(current, additions, retainedProperties);
        }
    }

    private (JsoncValueNode? ChildShape, ConfiglueModelSchema? ChildSchema) ResolveChildShape(
        string wireName,
        JsoncValueNode? shapeValue,
        ConfiglueModelSchema? currentSchema
    )
    {
        if (_schemaShape is null || currentSchema is null)
        {
            return (shapeValue, null);
        }

        // The detailed envelope stores the model payload under "$value". That child
        // keeps the same schema; other envelope/metadata keys have no nested schema.
        if (
            string.Equals(wireName, "$value", StringComparison.Ordinal)
            && ReferenceEquals(currentSchema, _schemaShape.RootSchema)
            && shapeValue?.Kind == JsonValueKind.Object
        )
        {
            return (shapeValue, currentSchema);
        }

        if (
            _schemaShape.TryGetNested(currentSchema, wireName, out var nested) && nested is not null
        )
        {
            if (shapeValue is null || shapeValue.Kind == JsonValueKind.Null)
            {
                // Shallow shapes record nested members as null. Fetch exactly one more
                // level for the finite document subtree. No recursion at shape-creation
                // time, so recursive schemas cannot overflow the stack. Shared DAG
                // schemas share one cache entry keyed by schema identity.
                return (_schemaShape.GetBareRootNode(nested), nested);
            }

            return (shapeValue, nested);
        }

        return (shapeValue, null);
    }

    private void AddArrayDiff(
        JsoncValueNode current,
        JsoncValueNode updated,
        byte[] updatedSource,
        ConfiglueModelSchema? elementSchema = null
    )
    {
        var currentItems = current.Items!;
        var updatedItems = updated.Items!;
        var sharedCount = Math.Min(currentItems.Count, updatedItems.Count);
        JsoncValueNode? elementShape = null;
        if (elementSchema is not null && _schemaShape is not null)
        {
            elementShape = _schemaShape.GetBareRootNode(elementSchema);
        }

        for (var index = 0; index < sharedCount; index++)
        {
            AddDiff(
                currentItems[index],
                updatedItems[index],
                elementShape,
                updatedSource,
                elementSchema
            );
        }

        if (updatedItems.Count < currentItems.Count)
        {
            for (var index = currentItems.Count - 1; index >= updatedItems.Count; index--)
            {
                AddArrayItemRemoval(current, index);
            }
        }
        else if (updatedItems.Count > currentItems.Count)
        {
            AddArrayItems(current, updatedItems.Skip(currentItems.Count), updatedSource);
        }
    }

    private void AddArrayItems(
        JsoncValueNode current,
        IEnumerable<JsoncValueNode> items,
        byte[] updatedSource
    )
    {
        var additions = items
            .Select(item => updatedSource.AsSpan(item.Start, item.End - item.Start).ToArray())
            .ToArray();
        if (
            current.Items!.Count > 0
            && !HasTrailingComma(current.Items[^1].End, current.CloseStart)
        )
        {
            _edits.Add(new TextEdit(current.Items[^1].End, 0, ","u8.ToArray()));
        }

        var insertion = BuildArrayInsertion(current, additions);
        _edits.Add(new TextEdit(insertion.Position, 0, insertion.Content));
    }

    private (int Position, byte[] Content) BuildArrayInsertion(
        JsoncValueNode array,
        IReadOnlyList<byte[]> additions
    )
    {
        if (HasLineBreak(Source, array.OpenEnd, array.CloseStart))
        {
            var lineStart = FindLineStart(Source, array.CloseStart);
            var indent = GetIndentation(Source, lineStart, array.CloseStart);
            var itemIndent =
                array.Items!.Count == 0
                    ? indent + GetIndentationUnit()
                    : GetIndentation(Source, array.Items[0].Start);
            var newline = FindNewline(Source);
            return (
                lineStart,
                JoinUtf8Fragments(additions, itemIndent, newline + itemIndent, newline)
            );
        }

        var prefix = array.Items!.Count == 0 ? "" : " ";
        return (array.CloseStart, JoinUtf8Fragments(additions, prefix, ", ", ""));
    }

    private void AddArrayItemRemoval(JsoncValueNode array, int index)
    {
        var items = array.Items!;
        var item = items[index];
        var comments = ExtractComments(Source, item.Start, item.End);
        _edits.Add(new TextEdit(item.Start, item.End - item.Start, comments));
        if (index > 0)
        {
            var comma = JsoncSyntaxTree.FindComma(Source, items[index - 1].End, item.Start);
            if (comma >= 0)
            {
                _edits.Add(new TextEdit(comma, 1, []));
            }
        }
        else if (items.Count == 1)
        {
            var trailingComma = JsoncSyntaxTree.FindComma(Source, item.End, array.CloseStart);
            if (trailingComma >= 0)
            {
                _edits.Add(new TextEdit(trailingComma, 1, []));
            }
        }
    }

    private void AddPropertyRemoval(JsoncPropertyNode property)
    {
        var comments = ExtractComments(Source, property.NameStart, property.Value.End);
        _edits.Add(
            new TextEdit(property.NameStart, property.Value.End - property.NameStart, comments)
        );
    }

    private void RewriteSeparators(
        JsoncValueNode current,
        IReadOnlyList<JsoncPropertyNode> retained,
        bool hasAdditions
    )
    {
        var properties = current.Properties!;
        var retainedIndexes = retained
            .Select(property => properties.IndexOf(property))
            .OrderBy(static index => index)
            .ToArray();
        var commasToKeep = new HashSet<int>();
        for (var index = 1; index < retainedIndexes.Length; index++)
        {
            var separatorIndex = retainedIndexes[index] - 1;
            if (properties[separatorIndex].CommaAfter is >= 0 and var comma)
            {
                commasToKeep.Add(comma);
            }
        }

        if (retainedIndexes.Length > 0 && hasAdditions)
        {
            var lastIndex = retainedIndexes[^1];
            if (properties[lastIndex].CommaAfter is >= 0 and var comma)
            {
                commasToKeep.Add(comma);
            }
            else if (properties[lastIndex].CommaAfter is not >= 0)
            {
                _edits.Add(new TextEdit(retained[^1].Value.End, 0, ","u8.ToArray()));
            }
        }
        else if (
            retainedIndexes.Length > 0
            && retainedIndexes[^1] == properties.Count - 1
            && properties[^1].CommaAfter is >= 0 and var trailingComma
        )
        {
            commasToKeep.Add(trailingComma);
        }

        foreach (var property in properties)
        {
            if (property.CommaAfter is >= 0 and var comma && !commasToKeep.Contains(comma))
            {
                _edits.Add(new TextEdit(comma, 1, []));
            }
        }
    }

    private void AddProperties(
        JsoncValueNode current,
        IReadOnlyList<AddedProperty> additions,
        IReadOnlyList<JsoncPropertyNode> retainedProperties
    )
    {
        var insertion = BuildObjectInsertion(current, additions, retainedProperties);
        _edits.Add(new TextEdit(insertion.Position, 0, insertion.Content));
    }

    private (int Position, byte[] Content) BuildObjectInsertion(
        JsoncValueNode value,
        IReadOnlyList<AddedProperty> additions,
        IReadOnlyList<JsoncPropertyNode> retainedProperties
    )
    {
        if (HasLineBreak(Source, value.OpenEnd, value.CloseStart))
        {
            var lineStart = FindLineStart(Source, value.CloseStart);
            var closeIndent = GetIndentation(Source, lineStart, value.CloseStart);
            var propertyIndent =
                retainedProperties.Count == 0
                    ? closeIndent + GetIndentationUnit()
                    : GetIndentation(Source, retainedProperties[0].NameStart);
            var newline = FindNewline(Source);
            return (
                lineStart,
                JoinUtf8Fragments(
                    additions.Select(static property => property.KeyAndValue).ToArray(),
                    propertyIndent,
                    "," + newline + propertyIndent,
                    newline
                )
            );
        }

        var prefix = retainedProperties.Count == 0 ? "" : " ";
        return (
            value.CloseStart,
            JoinUtf8Fragments(
                additions.Select(static property => property.KeyAndValue).ToArray(),
                prefix,
                ", ",
                ""
            )
        );
    }

    private static byte[] JoinUtf8Fragments(
        IReadOnlyList<byte[]> fragments,
        string prefix,
        string separator,
        string suffix
    )
    {
        var output = new ArrayBufferWriter<byte>();
        WriteUtf8(output, prefix);
        for (var index = 0; index < fragments.Count; index++)
        {
            if (index > 0)
            {
                WriteUtf8(output, separator);
            }

            var fragment = fragments[index];
            fragment.CopyTo(output.GetSpan(fragment.Length));
            output.Advance(fragment.Length);
        }

        WriteUtf8(output, suffix);
        return output.WrittenSpan.ToArray();
    }

    private static void WriteUtf8(ArrayBufferWriter<byte> output, string value)
    {
        var destination = output.GetSpan(Encoding.UTF8.GetMaxByteCount(value.Length));
        var bytes = Encoding.UTF8.GetBytes(value);
        bytes.CopyTo(destination);
        var written = bytes.Length;
        output.Advance(written);
    }

    private void AddReplacement(
        JsoncValueNode current,
        JsoncValueNode updated,
        byte[] updatedSource
    ) =>
        _edits.Add(
            new TextEdit(
                current.Start,
                current.End - current.Start,
                updatedSource.AsSpan(updated.Start, updated.End - updated.Start).ToArray()
            )
        );

    private byte[] ApplyEdits()
    {
        var result = Source;
        foreach (
            var edit in _edits
                .Select(static (edit, index) => (edit, index))
                .OrderByDescending(static item => item.edit.Start)
                .ThenByDescending(static item => item.index)
                .Select(static item => item.edit)
        )
        {
            var updated = new byte[result.Length - edit.Length + edit.Content.Length];
            result.AsSpan(0, edit.Start).CopyTo(updated);
            edit.Content.CopyTo(updated.AsSpan(edit.Start));
            result
                .AsSpan(edit.Start + edit.Length)
                .CopyTo(updated.AsSpan(edit.Start + edit.Content.Length));
            result = updated;
        }

        return result;
    }

    private static byte[] ExtractComments(byte[] source, int start, int end)
    {
        using var destination = new MemoryStream();
        var inString = false;
        var index = start;
        while (index < end)
        {
            if (inString)
            {
                if (source[index] == (byte)'\\')
                {
                    index += 2;
                }
                else
                {
                    if (source[index] == (byte)'"')
                    {
                        inString = false;
                    }

                    index++;
                }

                continue;
            }

            if (source[index] == (byte)'"')
            {
                inString = true;
                index++;
                continue;
            }

            if (source[index] != (byte)'/' || index + 1 >= end)
            {
                index++;
                continue;
            }

            var commentStart = index;
            if (source[index + 1] == (byte)'/')
            {
                index += 2;
                while (index < end && source[index] is not (byte)'\r' and not (byte)'\n')
                {
                    index++;
                }

                destination.Write(source, commentStart, index - commentStart);
                if (index < end && source[index] == (byte)'\r')
                {
                    destination.WriteByte((byte)'\r');
                    index++;
                }

                if (index < end && source[index] == (byte)'\n')
                {
                    destination.WriteByte((byte)'\n');
                    index++;
                }

                continue;
            }

            if (source[index + 1] == (byte)'*')
            {
                index += 2;
                while (
                    index + 1 < end
                    && (source[index] != (byte)'*' || source[index + 1] != (byte)'/')
                )
                {
                    index++;
                }

                if (index + 1 < end)
                {
                    index += 2;
                }

                destination.Write(source, commentStart, index - commentStart);
                if (index < end && source[index] is not (byte)'\r' and not (byte)'\n')
                {
                    destination.WriteByte((byte)' ');
                }
                else if (index < end)
                {
                    destination.WriteByte(source[index++]);
                    if (
                        index < end
                        && source[index - 1] == (byte)'\r'
                        && source[index] == (byte)'\n'
                    )
                    {
                        destination.WriteByte(source[index++]);
                    }
                }

                continue;
            }

            index++;
        }

        return destination.ToArray();
    }

    private bool Equivalent(JsoncValueNode left, JsoncValueNode right, byte[] rightSource)
    {
        var rightValue = rightSource.AsSpan(right.Start, right.End - right.Start);
        return Source.AsSpan(left.Start, left.End - left.Start).SequenceEqual(rightValue);
    }

    private bool HasTrailingComma(int start, int end) =>
        JsoncSyntaxTree.FindComma(Source, start, end) >= 0;

    private string GetIndentationUnit() =>
        new string(
            _serializerOptions.WriteIndented ? _serializerOptions.IndentCharacter : ' ',
            _serializerOptions.WriteIndented ? _serializerOptions.IndentSize : 2
        );

    private static bool HasLineBreak(byte[] source, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (source[index] is (byte)'\r' or (byte)'\n')
            {
                return true;
            }
        }

        return false;
    }

    private static int FindLineStart(byte[] source, int position)
    {
        var start = position;
        while (start > 0 && source[start - 1] is not (byte)'\r' and not (byte)'\n')
        {
            start--;
        }

        return start;
    }

    private static string GetIndentation(byte[] source, int start, int end)
    {
        var length = 0;
        while (start + length < end && source[start + length] is (byte)' ' or (byte)'\t')
        {
            length++;
        }

        return Encoding.UTF8.GetString(source, start, length);
    }

    private static string GetIndentation(byte[] source, int position) =>
        GetIndentation(source, FindLineStart(source, position), position);

    private static string FindNewline(byte[] source)
    {
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == (byte)'\r')
            {
                return index + 1 < source.Length && source[index + 1] == (byte)'\n' ? "\r\n" : "\r";
            }

            if (source[index] == (byte)'\n')
            {
                return "\n";
            }
        }

        return Environment.NewLine;
    }

    private sealed class AddedProperty(byte[] keyAndValue)
    {
        public byte[] KeyAndValue { get; } = keyAndValue;
    }

    private sealed class TextEdit(int start, int length, byte[] content)
    {
        public int Start { get; } = start;

        public int Length { get; } = length;

        public byte[] Content { get; } = content;
    }
}

internal sealed class JsonSchemaShape
{
    private readonly JsonSerializerOptions _bareOptions;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        BareLevel
    > _bareCache = new(StringComparer.Ordinal);

    private JsonSchemaShape(
        ConfiglueModelSchema rootSchema,
        byte[] rootShapeBytes,
        JsoncValueNode rootNode,
        JsonSerializerOptions bareOptions
    )
    {
        RootSchema = rootSchema;
        RootShapeBytes = rootShapeBytes;
        RootNode = rootNode;
        _bareOptions = bareOptions;
    }

    internal ConfiglueModelSchema RootSchema { get; }

    internal byte[] RootShapeBytes { get; }

    internal JsoncValueNode RootNode { get; }

    internal static JsonSchemaShape Create<TFragment>(
        ConfiglueModelSchema schema,
        JsonSerializerOptions? serializerOptions,
        DocumentLayoutOptions? layout,
        string? schemaReferenceBaseUri
    )
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        var rootBytes = JsoncDocumentEditor.CreateSchemaShape<TFragment>(
            schema,
            serializerOptions,
            layout,
            schemaReferenceBaseUri
        );
        var rootNode = JsoncSyntaxTree.Parse(rootBytes).Root;
        var bareOptions = serializerOptions is null
            ? new JsonSerializerOptions()
            : new JsonSerializerOptions(serializerOptions);
        JsonStateCodecOperations.EnsureTypeInfoResolver(bareOptions);
        return new JsonSchemaShape(schema, rootBytes, rootNode, bareOptions);
    }

    internal bool TryGetNested(
        ConfiglueModelSchema parent,
        string wireName,
        out ConfiglueModelSchema? nested
    )
    {
        nested = null;
        var level = GetBareLevel(parent);
        return level.NestedByWire.TryGetValue(wireName, out nested);
    }

    internal JsoncValueNode GetBareRootNode(ConfiglueModelSchema schema) =>
        GetBareLevel(schema).Root;

    private BareLevel GetBareLevel(ConfiglueModelSchema schema) =>
        _bareCache.GetOrAdd(
            CacheKey(schema),
            static (_, state) => CreateBareLevel(state.Options, state.Schema),
            (Options: _bareOptions, Schema: schema)
        );

    private static string CacheKey(ConfiglueModelSchema schema) =>
        string.Concat(
            schema.Id,
            "\0",
            schema.Version,
            "\0",
            schema.ModelType.FullName ?? schema.ModelType.Name
        );

    private static BareLevel CreateBareLevel(
        JsonSerializerOptions bareOptions,
        ConfiglueModelSchema schema
    )
    {
        var fragment = CreateShallowFragment(schema);
        byte[] bytes;
        using (var stream = new MemoryStream())
        {
            using var writer = new Utf8JsonWriter(stream);
            JsonSerializer.Serialize(writer, fragment, fragment.GetType(), bareOptions);
            writer.Flush();
            bytes = stream.ToArray();
        }

        using var document = JsonDocument.Parse(bytes, JsoncSyntaxTree.DocumentOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The generated JSON bare shape must be an object.");
        }

        var wireNames = document.RootElement.EnumerateObject().Select(static p => p.Name).ToArray();
        if (wireNames.Length != schema.Members.Count)
        {
            throw new JsonException(
                $"The generated JSON bare shape for schema '{schema.Id}' has an unexpected member count."
            );
        }

        var nestedByWire = new Dictionary<string, ConfiglueModelSchema>(StringComparer.Ordinal);
        for (var i = 0; i < schema.Members.Count; i++)
        {
            var member = schema.Members[i];
            if (member.NestedSchemaFactory is { } factory)
            {
                ConfiglueModelSchema nested;
                try
                {
                    nested = factory();
                }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    throw new InvalidOperationException(
                        $"Failed to resolve nested schema for member '{member.Name}' in schema '{schema.Id}'.",
                        exception
                    );
                }

                nestedByWire[wireNames[i]] = nested;
            }
        }

        var tree = JsoncSyntaxTree.Parse(bytes);
        return new BareLevel(tree.Root, nestedByWire);
    }

    private static IConfiglueFragment CreateShallowFragment(ConfiglueModelSchema schema)
    {
        var fragment = schema.CreateEmptyFragment();
        foreach (var member in schema.Members)
        {
            object? value = member.NestedSchemaFactory is not null
                ? null
                : member.DefaultValueFactory?.Invoke();
            fragment = fragment.WithMember(member.Id, value);
        }

        return fragment;
    }

    private sealed class BareLevel(
        JsoncValueNode root,
        Dictionary<string, ConfiglueModelSchema> nestedByWire
    )
    {
        public JsoncValueNode Root { get; } = root;

        public Dictionary<string, ConfiglueModelSchema> NestedByWire { get; } = nestedByWire;
    }
}
