using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Configlue.Provider.Json;

/// <summary>A JSON state codec with optional schema metadata stored beside the payload.</summary>
public sealed class JsonStateCodec
    : IStateCodec,
        IStateSchemaMetadataReader,
        IStateCodecRecoveryPolicy
{
    private readonly JsonSerializerOptions _options;
    private readonly DocumentLayoutOptions? _layout;

    /// <summary>Creates a codec with the supplied System.Text.Json options.</summary>
    public JsonStateCodec(
        JsonSerializerOptions? options = null,
        DocumentLayoutOptions? documentLayout = null
    )
    {
        _options = options is null
            ? new JsonSerializerOptions()
            : new JsonSerializerOptions(options);
        _layout = documentLayout;
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode(
        "Reflection-based JSON serialization may require types that trimming removes. Use JsonStateCodec<T> with JsonTypeInfo<T> for trim-safe serialization."
    )]
    [RequiresDynamicCode(
        "Reflection-based JSON serialization may require runtime code generation. Use JsonStateCodec<T> with JsonTypeInfo<T> for NativeAOT."
    )]
    public object? Deserialize(
        Type type,
        in ReadOnlySequence<byte> source,
        in StateCodecContext context
    )
    {
        ArgumentNullException.ThrowIfNull(type);
        var payload = JsonStateCodecOperations.GetPayload(in source, _layout, _options);
        var reader = new Utf8JsonReader(payload, JsoncSyntaxTree.ReaderOptions);
        return JsonSerializer.Deserialize(ref reader, type, _options);
    }

    /// <inheritdoc />
    [RequiresUnreferencedCode(
        "Reflection-based JSON serialization may require types that trimming removes. Use JsonStateCodec<T> with JsonTypeInfo<T> for trim-safe serialization."
    )]
    [RequiresDynamicCode(
        "Reflection-based JSON serialization may require runtime code generation. Use JsonStateCodec<T> with JsonTypeInfo<T> for NativeAOT."
    )]
    public void Serialize(
        Type type,
        object? value,
        IBufferWriter<byte> destination,
        in StateCodecContext context
    )
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(destination);
        var raw = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(raw))
        {
            JsonSerializer.Serialize(writer, value, type, _options);
            writer.Flush();
        }

        var schema =
            context.Schema
            ?? (value is IConfiglueFragment fragment ? fragment.Schema.ToMetadata() : null);
        var effectiveContext = schema is { } metadata
            ? new StateCodecContext(metadata, context.Services, context.SchemaReferenceBaseUri)
            : context;
        JsonStateCodecOperations.WritePayload(
            raw.WrittenMemory,
            destination,
            in effectiveContext,
            _layout
        );
    }

    /// <inheritdoc />
    public StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source) =>
        JsonStateCodecOperations.ReadSchemaMetadata(in source, _layout, _options);

    /// <inheritdoc />
    public bool IsRecoverableReadException(Exception exception) => exception is JsonException;
}

/// <summary>A typed JSON fast path for a state codec.</summary>
public sealed class JsonStateCodec<T>
    : IStateCodec<T>,
        IPipelineStateCodec<T>,
        IStateSchemaMetadataReader,
        IStateCodecRecoveryPolicy
{
    private readonly JsonSerializerOptions _options;
    private readonly JsonTypeInfo<T>? _typeInfo;
    private readonly JsonConverter<T>? _converter;
    private readonly JsonSerializerOptions _pipelineOptions;
    private readonly DocumentLayoutOptions? _layout;

    /// <summary>
    /// Gets or sets whether JSON should deserialize directly from pipeline streams. Benchmarks show that
    /// this path is workload-dependent, so it is disabled by default.
    /// </summary>
    public bool UseAsyncStreamDecoding { get; init; }

    bool IPipelineStateCodec<T>.IsPipelineDecodePreferred => UseAsyncStreamDecoding;

    /// <summary>Creates a reflection-based codec that uses the supplied options.</summary>
    /// <remarks>For trimming and NativeAOT, use the constructor that accepts <see cref="JsonTypeInfo{T}"/>.</remarks>
    [RequiresUnreferencedCode(
        "Reflection-based JSON serialization may require types that trimming removes. Use the JsonTypeInfo constructor for trim-safe serialization."
    )]
    [RequiresDynamicCode(
        "Reflection-based JSON serialization may require runtime code generation. Use the JsonTypeInfo constructor for NativeAOT."
    )]
    public JsonStateCodec(
        JsonSerializerOptions? options = null,
        DocumentLayoutOptions? documentLayout = null
    )
    {
        _options = options is null
            ? new JsonSerializerOptions()
            : new JsonSerializerOptions(options);
        EnsureTypeInfoResolver(_options);
        _pipelineOptions = CreatePipelineOptions(_options);
        _layout = documentLayout;
    }

    /// <summary>Creates a codec that uses source-generated or otherwise preconfigured type metadata.</summary>
    public JsonStateCodec(JsonTypeInfo<T> typeInfo, DocumentLayoutOptions? documentLayout = null)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        _typeInfo = typeInfo;
        _options = typeInfo.Options;
        _pipelineOptions = CreatePipelineOptions(_options);
        _layout = documentLayout;
    }

    /// <summary>Creates a codec that uses a generated fragment converter.</summary>
    public static JsonStateCodec<T> FromConverter(JsonConverter<T> converter)
    {
        ArgumentNullException.ThrowIfNull(converter);
        return new JsonStateCodec<T>(null, converter, null);
    }

    internal JsonStateCodec(
        JsonSerializerOptions? options,
        JsonConverter<T>? converter,
        DocumentLayoutOptions? documentLayout
    )
    {
        _options = options is null
            ? new JsonSerializerOptions()
            : new JsonSerializerOptions(options);
        EnsureTypeInfoResolver(_options);
        _converter = converter;
        _pipelineOptions = CreatePipelineOptions(_options);
        if (converter is not null)
        {
            _pipelineOptions.Converters.Insert(0, converter);
        }

        _layout = documentLayout;
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "The reflection resolver is created only when reflection-based serialization is enabled. NativeAOT applications must supply a source-generated resolver, which bypasses this branch."
    )]
    [UnconditionalSuppressMessage(
        "Aot",
        "IL3050",
        Justification = "The reflection resolver is created only when reflection-based serialization is enabled. NativeAOT applications must supply a source-generated resolver, which bypasses this branch."
    )]
    private static void EnsureTypeInfoResolver(JsonSerializerOptions options)
    {
        if (options.TypeInfoResolver is not null)
        {
            return;
        }

        if (!JsonSerializer.IsReflectionEnabledByDefault)
        {
            throw new InvalidOperationException(
                "A source-generated JsonSerializerContext must be supplied for JSON facade sources when reflection-based JSON serialization is disabled."
            );
        }

        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
    }

    private static JsonSerializerOptions CreatePipelineOptions(JsonSerializerOptions options) =>
        new(options) { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };

    /// <inheritdoc />
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "The options-based constructor carries this requirement. The JsonTypeInfo constructor sets _typeInfo and never enters the reflection fallback."
    )]
    [UnconditionalSuppressMessage(
        "Aot",
        "IL3050",
        Justification = "The options-based constructor carries this requirement. The JsonTypeInfo constructor sets _typeInfo and never enters the reflection fallback."
    )]
    public T? Deserialize(in ReadOnlySequence<byte> source, in StateCodecContext context)
    {
        var payload = JsonStateCodecOperations.GetPayload(in source, _layout, _options);
        var reader = new Utf8JsonReader(payload, JsoncSyntaxTree.ReaderOptions);
        if (_converter is not null)
        {
            if (!reader.Read())
            {
                throw new JsonException("The JSON payload is empty.");
            }

            if (reader.TokenType == JsonTokenType.Null && !_converter.HandleNull)
            {
                return default;
            }

            return _converter.Read(ref reader, typeof(T), _options);
        }

        return _typeInfo is null
            ? JsonSerializer.Deserialize<T>(ref reader, _options)
            : JsonSerializer.Deserialize(ref reader, _typeInfo);
    }

    /// <inheritdoc />
    public async ValueTask<StateReadResult<T>> DeserializeAsync(
        PipeReader content,
        StateCodecContext context,
        StateSchemaMetadata? resourceSchema,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(content);
        if (_options.UnmappedMemberHandling == JsonUnmappedMemberHandling.Disallow)
        {
            await using var strictStream = content.AsStream(leaveOpen: true);
            using var document = await JsonDocument
                .ParseAsync(strictStream, JsoncSyntaxTree.DocumentOptions, cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            var strictSchema =
                resourceSchema
                ?? JsonStateCodecOperations.ReadSchemaMetadataFromElement(root, _layout, _options)
                ?? context.Schema;
            var payload = JsonStateCodecOperations.GetPayloadElement(root);
            var filteredPayload =
                payload.ValueKind == JsonValueKind.Object
                && !JsonStateCodecOperations.IsMetadataEnvelope(root)
                    ? JsonStateCodecOperations.GetFilteredPayload(payload, _layout, _options)
                    : null;
            var strictValue = filteredPayload.HasValue
                ? JsonSerializer.Deserialize(
                    filteredPayload.Value.Span,
                    (JsonTypeInfo<T>)_pipelineOptions.GetTypeInfo(typeof(T))
                )
                : payload.Deserialize((JsonTypeInfo<T>)_pipelineOptions.GetTypeInfo(typeof(T)));
            return StateReadResult<T>.Success(strictValue, schema: strictSchema);
        }

        await using var source = content.AsStream(leaveOpen: true);
        using var capturingStream = new CapturingJsonStream(source);
        T? value = default;
        JsonException? deserializeException = null;
        try
        {
            value = await JsonSerializer
                .DeserializeAsync(
                    capturingStream,
                    (JsonTypeInfo<T>)_pipelineOptions.GetTypeInfo(typeof(T)),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            deserializeException = exception;
        }

        await capturingStream.DrainAsync(cancellationToken).ConfigureAwait(false);
        var bytes = capturingStream.CapturedContent;
        StateSchemaMetadata? schemaFromContent;
        bool isMetadataEnvelope;
        if (resourceSchema is null)
        {
            schemaFromContent = JsonStateCodecOperations.ReadSchemaMetadataAndEnvelope(
                in bytes,
                _layout,
                _options,
                out isMetadataEnvelope
            );
        }
        else
        {
            schemaFromContent = null;
            isMetadataEnvelope = JsonStateCodecOperations.IsMetadataEnvelope(in bytes);
        }

        var schema = resourceSchema ?? schemaFromContent ?? context.Schema;
        if (isMetadataEnvelope)
        {
            var effectiveContext = schema is { } metadata
                ? new StateCodecContext(metadata, context.Services, context.SchemaReferenceBaseUri)
                : context;
            value = Deserialize(in bytes, in effectiveContext);
        }
        else if (deserializeException is not null)
        {
            ExceptionDispatchInfo.Capture(deserializeException).Throw();
        }

        return StateReadResult<T>.Success(value, schema: schema);
    }

    private sealed class CapturingJsonStream : Stream
    {
        private readonly Stream _source;
        private readonly byte[] _prefix = new byte[3];
        private byte[]? _captured;
        private int _capturedLength;
        private int _prefixCount;
        private int _prefixOffset;
        private bool _prefixInitialized;

        public CapturingJsonStream(Stream source) => _source = source;

        public ReadOnlySequence<byte> CapturedContent =>
            _capturedLength == 0
                ? ReadOnlySequence<byte>.Empty
                : new ReadOnlySequence<byte>(_captured!.AsMemory(0, _capturedLength));

        public async ValueTask DrainAsync(CancellationToken cancellationToken)
        {
            var rented = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                while (
                    await ReadAsync(rented.AsMemory(), cancellationToken).ConfigureAwait(false) > 0
                )
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            await InitializePrefixAsync(cancellationToken).ConfigureAwait(false);
            var prefixRemaining = _prefixCount - _prefixOffset;
            if (prefixRemaining > 0)
            {
                var copied = Math.Min(prefixRemaining, buffer.Length);
                _prefix.AsMemory(_prefixOffset, copied).CopyTo(buffer);
                _prefixOffset += copied;
                return copied;
            }

            var read = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                Append(buffer.Span[..read]);
            }

            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private async ValueTask InitializePrefixAsync(CancellationToken cancellationToken)
        {
            if (_prefixInitialized)
            {
                return;
            }

            while (_prefixCount < _prefix.Length)
            {
                var read = await _source
                    .ReadAsync(_prefix.AsMemory(_prefixCount), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                _prefixCount += read;
                if (_prefix[0] != 0xEF || (_prefixCount >= 2 && _prefix[1] != 0xBB))
                {
                    break;
                }
            }

            Append(_prefix.AsSpan(0, _prefixCount));
            _prefixOffset =
                _prefixCount == 3 && _prefix[0] == 0xEF && _prefix[1] == 0xBB && _prefix[2] == 0xBF
                    ? 3
                    : 0;
            _prefixInitialized = true;
        }

        private void Append(ReadOnlySpan<byte> content)
        {
            if (content.IsEmpty)
            {
                return;
            }

            var requiredLength = checked(_capturedLength + content.Length);
            if (_captured is null || requiredLength > _captured.Length)
            {
                var capacity = Math.Max(
                    requiredLength,
                    _captured is null ? 4096 : checked(_captured.Length * 2)
                );
                var replacement = ArrayPool<byte>.Shared.Rent(capacity);
                if (_captured is not null)
                {
                    _captured.AsSpan(0, _capturedLength).CopyTo(replacement);
                    ArrayPool<byte>.Shared.Return(_captured, clearArray: true);
                }

                _captured = replacement;
            }

            content.CopyTo(_captured.AsSpan(_capturedLength));
            _capturedLength = requiredLength;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("JSON pipeline decoding requires asynchronous reads.");

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && _captured is not null)
            {
                ArrayPool<byte>.Shared.Return(_captured, clearArray: true);
                _captured = null;
                _capturedLength = 0;
            }

            base.Dispose(disposing);
        }
    }

    /// <inheritdoc />
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "The options-based constructor carries this requirement. The JsonTypeInfo constructor sets _typeInfo and never enters the reflection fallback."
    )]
    [UnconditionalSuppressMessage(
        "Aot",
        "IL3050",
        Justification = "The options-based constructor carries this requirement. The JsonTypeInfo constructor sets _typeInfo and never enters the reflection fallback."
    )]
    public void Serialize(T? value, IBufferWriter<byte> destination, in StateCodecContext context)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var raw = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(raw))
        {
            if (_converter is not null)
            {
                if (value is null && !_converter.HandleNull)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    _converter.Write(writer, value!, _options);
                }
            }
            else if (_typeInfo is null)
            {
                JsonSerializer.Serialize(writer, value, _options);
            }
            else
            {
                JsonSerializer.Serialize<T>(writer, value!, _typeInfo);
            }

            writer.Flush();
        }

        var schema =
            context.Schema
            ?? (value is IConfiglueFragment fragment ? fragment.Schema.ToMetadata() : null);
        var effectiveContext = schema is { } metadata
            ? new StateCodecContext(metadata, context.Services, context.SchemaReferenceBaseUri)
            : context;
        JsonStateCodecOperations.WritePayload(
            raw.WrittenMemory,
            destination,
            in effectiveContext,
            _layout
        );
    }

    /// <inheritdoc />
    public StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source) =>
        JsonStateCodecOperations.ReadSchemaMetadata(in source, _layout, _options);

    /// <inheritdoc />
    public bool IsRecoverableReadException(Exception exception) => exception is JsonException;
}

internal static partial class JsonStateCodecOperations
{
    private const string MetadataProperty = "$configlue";
    private const string PayloadProperty = "$value";
    private const string SchemaProperty = "$schema";
    private const string DefaultVersionProperty = "$version";

    public static ReadOnlySequence<byte> GetPayload(
        in ReadOnlySequence<byte> source,
        DocumentLayoutOptions? layout,
        JsonSerializerOptions? serializerOptions
    )
    {
        var normalizedSource = StripUtf8Bom(source);
        if (!HasMetadataEnvelope(in normalizedSource))
        {
            return StripSimpleDocument(in normalizedSource, layout, serializerOptions);
        }

        using var document = JsonDocument.Parse(normalizedSource, JsoncSyntaxTree.DocumentOptions);
        if (!document.RootElement.TryGetProperty(PayloadProperty, out var payload))
        {
            throw new JsonException(
                $"A '{MetadataProperty}' metadata envelope must contain a '{PayloadProperty}' value."
            );
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            payload.WriteTo(writer);
        }

        return new ReadOnlySequence<byte>(buffer.WrittenMemory);
    }

    public static StateSchemaMetadata? ReadSchemaMetadata(
        in ReadOnlySequence<byte> source,
        DocumentLayoutOptions? layout,
        JsonSerializerOptions? serializerOptions
    )
    {
        var normalizedSource = StripUtf8Bom(source);
        using var document = JsonDocument.Parse(normalizedSource, JsoncSyntaxTree.DocumentOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var root = document.RootElement;
        if (root.TryGetProperty(MetadataProperty, out var metadata))
        {
            if (
                metadata.ValueKind != JsonValueKind.Object
                || !metadata.TryGetProperty("version", out var versionElement)
                || !versionElement.TryGetInt32(out var version)
                || version < StateSchemaMetadata.InitialVersion
            )
            {
                throw new JsonException(
                    $"The '{MetadataProperty}' metadata must contain a positive integer version."
                );
            }

            var id =
                metadata.TryGetProperty("id", out var idElement)
                && idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString()
                    : null;
            return new StateSchemaMetadata(id, version);
        }

        return ReadSimpleVersion(root, layout, serializerOptions);
    }

    public static void WritePayload(
        ReadOnlyMemory<byte> serializedValue,
        IBufferWriter<byte> destination,
        in StateCodecContext context,
        DocumentLayoutOptions? layout
    )
    {
        if (context.Schema is not { } schema)
        {
            serializedValue.Span.CopyTo(destination.GetSpan(serializedValue.Length));
            destination.Advance(serializedValue.Length);
            return;
        }

        if (schema.Version < StateSchemaMetadata.InitialVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(context),
                "Schema versions must be positive."
            );
        }

        using var document = JsonDocument.Parse(serializedValue);
        if (
            (layout?.Layout ?? DocumentLayout.Simple) == DocumentLayout.Simple
            && document.RootElement.ValueKind == JsonValueKind.Object
        )
        {
            WriteSimplePayload(document.RootElement, schema, destination, in context, layout);
            return;
        }

        using var writer = new Utf8JsonWriter(destination);
        writer.WriteStartObject();
        WriteSchemaReference(writer, in context);
        writer.WritePropertyName(MetadataProperty);
        writer.WriteStartObject();
        if (schema.ModelId is not null)
        {
            writer.WriteString("id", schema.ModelId);
        }

        writer.WriteNumber("version", schema.Version);
        writer.WriteEndObject();
        writer.WritePropertyName(PayloadProperty);
        document.RootElement.WriteTo(writer);
        writer.WriteEndObject();
        writer.Flush();
    }

    private static void WriteSimplePayload(
        JsonElement payload,
        StateSchemaMetadata schema,
        IBufferWriter<byte> destination,
        in StateCodecContext context,
        DocumentLayoutOptions? layout
    )
    {
        using var writer = new Utf8JsonWriter(destination);
        writer.WriteStartObject();
        writer.WriteNumber(layout?.VersionProperty ?? DefaultVersionProperty, schema.Version);
        if (context.SchemaReferenceBaseUri is not null)
        {
            WriteSchemaReference(writer, in context);
        }

        foreach (var property in payload.EnumerateObject())
        {
            property.WriteTo(writer);
        }

        writer.WriteEndObject();
        writer.Flush();
    }

    private static void WriteSchemaReference(Utf8JsonWriter writer, in StateCodecContext context)
    {
        if (context.SchemaReferenceBaseUri is not { } schemaReferenceBaseUri)
        {
            return;
        }

        if (context.Schema is not { } referencedSchema)
        {
            throw new InvalidOperationException(
                "A schema reference base URI requires schema metadata."
            );
        }

        writer.WriteString(
            SchemaProperty,
            StateSchemaReference.CreateUri(schemaReferenceBaseUri, referencedSchema)
        );
    }

    private static bool HasMetadataEnvelope(in ReadOnlySequence<byte> source)
    {
        var probe = new Utf8JsonReader(source, JsoncSyntaxTree.ReaderOptions);
        if (!probe.Read() || probe.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        while (probe.Read())
        {
            if (probe.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (probe.TokenType != JsonTokenType.PropertyName)
            {
                return false;
            }

            if (probe.ValueTextEquals(MetadataProperty))
            {
                return true;
            }

            if (!probe.Read())
            {
                return false;
            }

            probe.Skip();
        }

        return false;
    }

    private static ReadOnlySequence<byte> StripUtf8Bom(in ReadOnlySequence<byte> source)
    {
        if (source.Length < 3)
        {
            return source;
        }

        var reader = new SequenceReader<byte>(source);
        if (
            !reader.TryRead(out var first)
            || first != 0xEF
            || !reader.TryRead(out var second)
            || second != 0xBB
            || !reader.TryRead(out var third)
            || third != 0xBF
        )
        {
            return source;
        }

        return source.Slice(reader.Position);
    }

    private static ReadOnlySequence<byte> StripSimpleDocument(
        in ReadOnlySequence<byte> source,
        DocumentLayoutOptions? layout,
        JsonSerializerOptions? serializerOptions
    )
    {
        using var document = JsonDocument.Parse(source, JsoncSyntaxTree.DocumentOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return source;
        }

        var root = document.RootElement;
        var versionName = FindProperty(root, layout, serializerOptions);
        if (versionName is null && !root.TryGetProperty(SchemaProperty, out _))
        {
            return source;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
#pragma warning disable S3267 // Keep this hot path free of a LINQ iterator allocation.
            foreach (var property in root.EnumerateObject())
            {
                if (
                    string.Equals(property.Name, versionName, StringComparison.Ordinal)
                    || string.Equals(property.Name, SchemaProperty, StringComparison.Ordinal)
                )
                {
                    continue;
                }

                property.WriteTo(writer);
            }
#pragma warning restore S3267

            writer.WriteEndObject();
        }

        return new ReadOnlySequence<byte>(buffer.WrittenMemory);
    }

    private static StateSchemaMetadata? ReadSimpleVersion(
        JsonElement root,
        DocumentLayoutOptions? layout,
        JsonSerializerOptions? serializerOptions
    )
    {
        var modelId = layout?.ModelId;
        var versionName = FindProperty(root, layout, serializerOptions);
        if (versionName is null)
        {
            // A schema-annotated document without a version defaults to version 1. A document
            // without any marker keeps the legacy bare behavior, unless a model ID opts the
            // reader into legacy version attribution for migration.
            return root.TryGetProperty(SchemaProperty, out _) || modelId is not null
                ? new StateSchemaMetadata(modelId, StateSchemaMetadata.InitialVersion)
                : null;
        }

        if (
            !root.TryGetProperty(versionName, out var versionValue)
            || versionValue.ValueKind != JsonValueKind.Number
            || !versionValue.TryGetInt32(out var version)
            || version < StateSchemaMetadata.InitialVersion
        )
        {
            throw new JsonException(
                $"The version property '{versionName}' must be a positive integer."
            );
        }

        return new StateSchemaMetadata(modelId, version);
    }

    private static string? FindProperty(
        JsonElement root,
        DocumentLayoutOptions? layout,
        JsonSerializerOptions? serializerOptions
    )
    {
        if (
            FindProperty(
                root,
                layout?.VersionProperty ?? DefaultVersionProperty,
                serializerOptions
            ) is
            { } versionName
        )
        {
            return versionName;
        }

        var fallbacks = layout?.FallbackVersionProperties ?? ["Version"];
        foreach (var fallback in fallbacks)
        {
            if (FindProperty(root, fallback, serializerOptions) is { } fallbackName)
            {
                return fallbackName;
            }
        }

        return null;
    }

    private static string? FindProperty(
        JsonElement root,
        string propertyName,
        JsonSerializerOptions? serializerOptions
    )
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            throw new ArgumentException(
                "A version property cannot be empty.",
                nameof(propertyName)
            );
        }

        if (root.TryGetProperty(propertyName, out _))
        {
            return propertyName;
        }

        var convertedName = serializerOptions?.PropertyNamingPolicy?.ConvertName(propertyName);
        if (
            convertedName is not null
            && !string.Equals(convertedName, propertyName, StringComparison.Ordinal)
            && root.TryGetProperty(convertedName, out _)
        )
        {
            return convertedName;
        }

        if (serializerOptions?.PropertyNameCaseInsensitive == true)
        {
            return root.EnumerateObject()
                .Select(property => property.Name)
                .FirstOrDefault(candidate =>
                    string.Equals(candidate, propertyName, StringComparison.OrdinalIgnoreCase)
                    || (
                        convertedName is not null
                        && string.Equals(
                            candidate,
                            convertedName,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                );
        }

        return null;
    }
}
