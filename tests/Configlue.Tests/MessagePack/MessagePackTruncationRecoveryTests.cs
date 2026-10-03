using System.Buffers;
using Configlue.Provider.MessagePack;
using MessagePack;
using MessagePack.Formatters;

namespace Configlue.Tests;

public sealed class MessagePackTruncationRecoveryTests
{
    [Test]
    public void TruncatedEnvelope_IsRecoverableAcrossTypedAndDynamicPaths()
    {
        (byte[] Bytes, bool ExpectTruncationInner)[] cases =
        [
            // Map header only.
            ([0x81], true),
            // Map header plus a partial key ("$v" of a 6-byte fixstr).
            ([0x81, 0xA6, 0x24, 0x76], true),
            // Map plus key "x" with the value missing.
            ([0x81, 0xA1, 0x78], true),
            // Envelope prefix truncated inside the "$configlue" metadata map.
            // The codec surfaces the reader's own payload exception here, which
            // is still classified as a recoverable payload failure.
            (
                [
                    0x82,
                    0xA9,
                    0x24,
                    0x63,
                    0x6F,
                    0x6E,
                    0x66,
                    0x69,
                    0x67,
                    0x6C,
                    0x75,
                    0x65,
                    0x82,
                    0xA7,
                    0x76,
                    0x65,
                    0x72,
                    0x73,
                    0x69,
                    0x6F,
                    0x6E,
                ],
                false
            ),
        ];

        var typed = new MessagePackStateCodec<int>();
        var dynamic = new MessagePackStateCodec();

        foreach (var (bytes, expectTruncationInner) in cases)
        {
            var sequence = new ReadOnlySequence<byte>(bytes);

            var valueException = Should.Throw<MessagePackSerializationException>(() =>
                typed.Deserialize(sequence, default)
            );
            AssertRecoverable(typed, dynamic, valueException, expectTruncationInner);

            var metadataException = Should.Throw<MessagePackSerializationException>(() =>
                typed.DeserializeWithMetadata(sequence, default)
            );
            AssertRecoverable(typed, dynamic, metadataException, expectTruncationInner);

            var schemaException = Should.Throw<MessagePackSerializationException>(() =>
                typed.ReadSchemaMetadata(sequence)
            );
            AssertRecoverable(typed, dynamic, schemaException, expectTruncationInner);

            var dynamicException = Should.Throw<MessagePackSerializationException>(() =>
                dynamic.Deserialize(typeof(int), sequence, default)
            );
            AssertRecoverable(typed, dynamic, dynamicException, expectTruncationInner);

            var dynamicSchemaException = Should.Throw<MessagePackSerializationException>(() =>
                dynamic.ReadSchemaMetadata(sequence)
            );
            AssertRecoverable(typed, dynamic, dynamicSchemaException, expectTruncationInner);
        }
    }

    [Test]
    public void TruncatedEnvelope_EveryPrefixRemainsRecoverable()
    {
        var full = SerializeInt(42);
        full.Length.ShouldBeGreaterThan(2);

        var typed = new MessagePackStateCodec<int>();
        var dynamic = new MessagePackStateCodec();

        for (var length = 1; length < full.Length; length++)
        {
            var sequence = new ReadOnlySequence<byte>(full[..length]);

            var valueException = Should.Throw<MessagePackSerializationException>(() =>
                typed.Deserialize(sequence, default)
            );
            AssertRecoverable(typed, dynamic, valueException, expectTruncationInner: false);

            var metadataException = Should.Throw<MessagePackSerializationException>(() =>
                typed.DeserializeWithMetadata(sequence, default)
            );
            AssertRecoverable(typed, dynamic, metadataException, expectTruncationInner: false);

            var schemaException = Should.Throw<MessagePackSerializationException>(() =>
                typed.ReadSchemaMetadata(sequence)
            );
            AssertRecoverable(typed, dynamic, schemaException, expectTruncationInner: false);

            var dynamicException = Should.Throw<MessagePackSerializationException>(() =>
                dynamic.Deserialize(typeof(int), sequence, default)
            );
            AssertRecoverable(typed, dynamic, dynamicException, expectTruncationInner: false);
        }

        typed.Deserialize(new ReadOnlySequence<byte>(full), default).ShouldBe(42);
    }

    [Test]
    public async Task TruncatedPrimary_RecoversFromValidBackup()
    {
        var resource = new BackupStubResource([0x81], SerializeInt(42));
        var reader = new SerializedStateReader<int>(
            resource,
            new MessagePackStateCodec<int>()
        );

        var result = await reader.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Value.ShouldBe(42);
        resource.RecoveryAttempts.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task TruncatedPrimary_SkipsInvalidBackup()
    {
        var resource = new BackupStubResource([0x81], [0x81, 0xA1, 0x78]);
        var codec = new MessagePackStateCodec<int>();
        var reader = new SerializedStateReader<int>(resource, codec);

        var exception = await Should.ThrowAsync<MessagePackSerializationException>(
            async () => await reader.ReadAsync()
        );

        codec.IsRecoverableReadException(exception).ShouldBeTrue();
        resource.RecoveryAttempts.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Test]
    public void PayloadFormatterFailures_PreserveOriginalExceptions()
    {
        var sequence = new ReadOnlySequence<byte>(SerializeInt(42));

        // An application formatter failure is nested by the MessagePack serializer;
        // the codec must preserve the original instance instead of replacing it.
        var customFailure = new InvalidOperationException("custom formatter failure");
        var customCodec = new MessagePackStateCodec<int>(
            MessagePackSerializerOptions.Standard.WithResolver(
                new ThrowingIntResolver(new ThrowingIntFormatter(customFailure))
            )
        );
        Should
            .Throw<MessagePackSerializationException>(() =>
                customCodec.Deserialize(sequence, default)
            )
            .InnerException.ShouldBeSameAs(customFailure);
        customCodec.IsRecoverableReadException(customFailure).ShouldBeFalse();

        var cancellation = new OperationCanceledException();
        var cancelCodec = new MessagePackStateCodec<int>(
            MessagePackSerializerOptions.Standard.WithResolver(
                new ThrowingIntResolver(new ThrowingIntFormatter(cancellation))
            )
        );
        Should
            .Throw<MessagePackSerializationException>(() =>
                cancelCodec.Deserialize(sequence, default)
            )
            .InnerException.ShouldBeSameAs(cancellation);
        cancelCodec.IsRecoverableReadException(cancellation).ShouldBeFalse();

        // A missing formatter stays visible as such and is never recoverable.
        var missingCodec = new MessagePackStateCodec<int>(
            MessagePackSerializerOptions.Standard.WithResolver(new ThrowingIntResolver(null))
        );
        Should
            .Throw<MessagePackSerializationException>(() =>
                missingCodec.Deserialize(sequence, default)
            )
            .InnerException.ShouldBeOfType<FormatterNotRegisteredException>();
        missingCodec
            .IsRecoverableReadException(
                new MessagePackSerializationException(
                    "outer",
                    new FormatterNotRegisteredException("missing")
                )
            )
            .ShouldBeFalse();
    }

    private static void AssertRecoverable(
        IStateCodecRecoveryPolicy typed,
        IStateCodecRecoveryPolicy dynamic,
        MessagePackSerializationException exception,
        bool expectTruncationInner
    )
    {
        typed.IsRecoverableReadException(exception).ShouldBeTrue();
        dynamic.IsRecoverableReadException(exception).ShouldBeTrue();
        if (expectTruncationInner)
        {
            (
                exception.InnerException
                    is EndOfStreamException or InsufficientExecutionStackException
            ).ShouldBeTrue();
        }
    }

    private static byte[] SerializeInt(int value)
    {
        var codec = new MessagePackStateCodec<int>();
        var buffer = new ArrayBufferWriter<byte>();
        codec.Serialize(value, buffer, default);
        return buffer.WrittenMemory.ToArray();
    }

    private sealed class BackupStubResource(byte[] primary, byte[]? backup)
        : IResourceReader,
            IResourceBackupRecovery
    {
        public int RecoveryAttempts { get; private set; }

        public bool AutomaticBackupRecoveryEnabled => true;

        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => new(ResourceReadResult.Success(primary));

        public async ValueTask<ResourceReadResult?> TryRecoverLatestBackupAsync(
            string? expectedRevision,
            bool expectedMissing,
            Func<ResourceReadResult, CancellationToken, ValueTask<bool>> validate,
            CancellationToken cancellationToken = default
        )
        {
            RecoveryAttempts++;
            if (backup is null)
            {
                return null;
            }

            var candidate = ResourceReadResult.Success(backup);
            return await validate(candidate, cancellationToken).ConfigureAwait(false)
                ? candidate
                : null;
        }
    }

    private sealed class ThrowingIntResolver(IMessagePackFormatter<int>? formatter)
        : IFormatterResolver
    {
        public IMessagePackFormatter<T>? GetFormatter<T>() =>
            formatter is null || typeof(T) != typeof(int)
                ? null
                : (IMessagePackFormatter<T>)(object)formatter;
    }
}

#pragma warning disable MsgPack013 // Test formatter is constructed per-case with the failure to surface.
internal sealed class ThrowingIntFormatter(Exception failure) : IMessagePackFormatter<int>
{
    public void Serialize(
        ref MessagePackWriter writer,
        int value,
        MessagePackSerializerOptions options
    ) => throw failure;

    public int Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) =>
        throw failure;
}
#pragma warning restore MsgPack013
