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
        JsonStateCodecOperations.EnsureTypeInfoResolver(_options);
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
        var schema =
            context.Schema
            ?? (value is IConfiglueFragment fragment ? fragment.Schema.ToMetadata() : null);
        if (schema is null)
        {
            using var directWriter = new Utf8JsonWriter(destination);
            JsonSerializer.Serialize(directWriter, value, type, _options);
            directWriter.Flush();
            return;
        }

        var envelopeContext = new StateCodecContext(
            schema.Value,
            context.Services,
            context.SchemaReferenceBaseUri
        );
        if (JsonStateCodecOperations.UsesEnvelopeLayout(_layout))
        {
            using var directWriter = new Utf8JsonWriter(destination);
            JsonStateCodecOperations.WriteEnvelopeStart(directWriter, in envelopeContext);
            JsonSerializer.Serialize(directWriter, value, type, _options);
            directWriter.WriteEndObject();
            directWriter.Flush();
            return;
        }

        var converter = _options.GetConverter(type);
        var typeInfo = converter is IJsonObjectPayloadWriter ? null : _options.GetTypeInfo(type);
        using (var directWriter = new Utf8JsonWriter(destination))
        {
            if (
                JsonStateCodecOperations.TryWriteSimpleObjectPayload(
                    directWriter,
                    value,
                    type,
                    converter,
                    typeInfo,
                    _options,
                    schema.Value,
                    in envelopeContext,
                    _layout
                )
            )
            {
                directWriter.Flush();
                return;
            }

            directWriter.Flush();
        }

        var raw = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(raw))
        {
            JsonSerializer.Serialize(writer, value, type, _options);
            writer.Flush();
        }

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
        IStateCodecWithMetadata<T>,
        IPipelineStateCodec<T>,
        IStateSchemaMetadataReader,
        IStateCodecRecoveryPolicy
{
    private readonly JsonSerializerOptions _options;
    private readonly JsonTypeInfo<T>? _typeInfo;
    private readonly JsonConverter<T>? _converter;
    private readonly JsonSerializerOptions _pipelineOptions;
    private readonly DocumentLayoutOptions? _layout;
    private readonly string[] _versionPropertyCandidates;

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
        _versionPropertyCandidates = JsonStateCodecOperations.GetVersionPropertyCandidates(
            documentLayout,
            _options
        );
    }

    /// <summary>Creates a codec that uses source-generated or otherwise preconfigured type metadata.</summary>
    public JsonStateCodec(JsonTypeInfo<T> typeInfo, DocumentLayoutOptions? documentLayout = null)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        _typeInfo = typeInfo;
        _options = typeInfo.Options;
        _pipelineOptions = CreatePipelineOptions(_options);
        _layout = documentLayout;
        _versionPropertyCandidates = JsonStateCodecOperations.GetVersionPropertyCandidates(
            documentLayout,
            _options
        );
    }

    /// <summary>Creates a codec that uses a generated fragment converter and default JSON options.</summary>
    /// <remarks>
    /// Generated fragment converters still require type metadata for scalar and collection member types. In
    /// NativeAOT applications, use the overload that accepts options with a source-generated type resolver.
    /// </remarks>
    public static JsonStateCodec<T> FromConverter(JsonConverter<T> converter)
    {
        ArgumentNullException.ThrowIfNull(converter);
        return new JsonStateCodec<T>(null, converter, null);
    }

    /// <summary>Creates a codec that uses a generated fragment converter and caller-supplied JSON metadata.</summary>
    /// <param name="converter">The generated converter for the fragment shape.</param>
    /// <param name="options">Options whose resolver provides metadata for non-generated member types.</param>
    /// <param name="documentLayout">Optional document layout configuration.</param>
    /// <remarks>
    /// For NativeAOT, <paramref name="options"/> should use a source-generated <c>JsonSerializerContext</c>
    /// covering every scalar and collection member type delegated by the fragment converter.
    /// </remarks>
    public static JsonStateCodec<T> FromConverter(
        JsonConverter<T> converter,
        JsonSerializerOptions options,
        DocumentLayoutOptions? documentLayout = null
    )
    {
        ArgumentNullException.ThrowIfNull(converter);
        ArgumentNullException.ThrowIfNull(options);
        return new JsonStateCodec<T>(options, converter, documentLayout);
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
        _versionPropertyCandidates = JsonStateCodecOperations.GetVersionPropertyCandidates(
            documentLayout,
            _options
        );
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
                "JSON facade serialization requires JsonSerializerOptions.TypeInfoResolver when reflection is disabled. Supply a source-generated JsonSerializerContext that covers the model and the scalar/collection member types used by Configlue's generated fragment converter."
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
    public StateCodecDecodeResult<T> DeserializeWithMetadata(
        in ReadOnlySequence<byte> source,
        in StateCodecContext context
    )
    {
        var normalizedSource = JsonStateCodecOperations.StripUtf8Bom(in source);
        using var document = JsonDocument.Parse(normalizedSource, JsoncSyntaxTree.DocumentOptions);
        var root = document.RootElement;
        var schema = JsonStateCodecOperations.ReadSchemaMetadataFromElement(
            root,
            _layout,
            _options
        );
        var payload = JsonStateCodecOperations.GetPayloadElement(root);
        var value =
            payload.ValueKind == JsonValueKind.Object
            && !JsonStateCodecOperations.IsMetadataEnvelope(root)
                ? JsonStateCodecOperations.GetFilteredPayload(payload, _layout, _options)
                : null;
        var decoded = value is { } filtered
            ? JsonSerializer.Deserialize(
                filtered.Span,
                (JsonTypeInfo<T>)_pipelineOptions.GetTypeInfo(typeof(T))
            )
            : payload.Deserialize((JsonTypeInfo<T>)_pipelineOptions.GetTypeInfo(typeof(T)));
        return new StateCodecDecodeResult<T>(decoded, schema);
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
#if NETSTANDARD
            using var strictStream = content.AsStream(leaveOpen: true);
#else
            await using var strictStream = content.AsStream(leaveOpen: true);
#endif
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
            return strictValue is null
                ? StateReadResult<T>.InvalidPayload(default) with
                {
                    Schema = strictSchema,
                }
                : StateReadResult<T>.Success(strictValue, schema: strictSchema);
        }

#if NETSTANDARD
        using var source = content.AsStream(leaveOpen: true);
#else
        await using var source = content.AsStream(leaveOpen: true);
#endif
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
                _versionPropertyCandidates,
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

        return value is null
            ? StateReadResult<T>.InvalidPayload(default) with
            {
                Schema = schema,
            }
            : StateReadResult<T>.Success(value, schema: schema);
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

        public CapturingJsonStream(Stream source)
        {
            _source = source;
            if (source.CanSeek && source.Length - source.Position is > 0 and <= int.MaxValue)
            {
                _captured = ArrayPool<byte>.Shared.Rent((int)(source.Length - source.Position));
            }
        }

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

#if NETSTANDARD2_0
        public async ValueTask<int> ReadAsync(
#else
        public override async ValueTask<int> ReadAsync(
#endif
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

#if NETSTANDARD2_0
            var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            int read;
            try
            {
                read = await _source
                    .ReadAsync(rented, 0, buffer.Length, cancellationToken)
                    .ConfigureAwait(false);
                rented.AsMemory(0, read).CopyTo(buffer);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
#else
            var read = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
#endif
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
                    .ReadAsync(
                        _prefix,
                        _prefixCount,
                        _prefix.Length - _prefixCount,
                        cancellationToken
                    )
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
        var schema =
            context.Schema
            ?? (value is IConfiglueFragment fragment ? fragment.Schema.ToMetadata() : null);
        if (schema is null)
        {
            using var directWriter = new Utf8JsonWriter(destination);
            WriteValue(directWriter, value);
            directWriter.Flush();
            return;
        }

        var envelopeContext = new StateCodecContext(
            schema.Value,
            context.Services,
            context.SchemaReferenceBaseUri
        );
        if (JsonStateCodecOperations.UsesEnvelopeLayout(_layout))
        {
            using var directWriter = new Utf8JsonWriter(destination);
            JsonStateCodecOperations.WriteEnvelopeStart(directWriter, in envelopeContext);
            WriteValue(directWriter, value);
            directWriter.WriteEndObject();
            directWriter.Flush();
            return;
        }

        var converter =
            (JsonConverter?)_converter ?? _typeInfo?.Converter ?? _options.GetConverter(typeof(T));
        var typeInfo =
            converter is IJsonObjectPayloadWriter
                ? null
                : _typeInfo ?? _options.GetTypeInfo(typeof(T));
        using (var directWriter = new Utf8JsonWriter(destination))
        {
            if (
                JsonStateCodecOperations.TryWriteSimpleObjectPayload(
                    directWriter,
                    value,
                    typeof(T),
                    converter,
                    typeInfo,
                    _options,
                    schema.Value,
                    in envelopeContext,
                    _layout
                )
            )
            {
                directWriter.Flush();
                return;
            }

            directWriter.Flush();
        }

        var raw = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(raw))
        {
            WriteValue(writer, value);
            writer.Flush();
        }

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

    private void WriteValue(Utf8JsonWriter writer, T? value)
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
    }

    /// <inheritdoc />
    public StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source) =>
        JsonStateCodecOperations.ReadSchemaMetadata(in source, _layout, _options);

    /// <inheritdoc />
    public bool IsRecoverableReadException(Exception exception) => exception is JsonException;
}
