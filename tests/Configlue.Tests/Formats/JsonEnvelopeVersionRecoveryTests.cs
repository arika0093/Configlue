using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Configlue.Provider.Json;

namespace Configlue.Tests;

public sealed class JsonEnvelopeVersionRecoveryTests
{
    [Test]
    [Arguments("\"bad\"")]
    [Arguments("null")]
    [Arguments("true")]
    [Arguments("{}")]
    [Arguments("[]")]
    [Arguments("0")]
    [Arguments("-2")]
    [Arguments("2147483648")]
    public void InvalidEnvelopeVersion_IsRecoverableJsonExceptionAcrossMetadataPaths(
        string versionJson
    )
    {
        var sequence = EnvelopeSequence(versionJson);
        var typed = new JsonStateCodec<int>();
        var dynamic = new JsonStateCodec();

        var typedMetadataException = Should.Throw<JsonException>(() =>
            typed.ReadSchemaMetadata(sequence)
        );
        typed.IsRecoverableReadException(typedMetadataException).ShouldBeTrue();

        var dynamicMetadataException = Should.Throw<JsonException>(() =>
            dynamic.ReadSchemaMetadata(sequence)
        );
        dynamic.IsRecoverableReadException(dynamicMetadataException).ShouldBeTrue();

        var combinedException = Should.Throw<JsonException>(() =>
            typed.DeserializeWithMetadata(sequence, default)
        );
        typed.IsRecoverableReadException(combinedException).ShouldBeTrue();
    }

    [Test]
    [Arguments("\"bad\"")]
    [Arguments("null")]
    [Arguments("true")]
    [Arguments("{}")]
    [Arguments("[]")]
    [Arguments("0")]
    [Arguments("-2")]
    [Arguments("2147483648")]
    public async Task InvalidEnvelopeVersion_IsRecoverableJsonExceptionAcrossStreamPaths(
        string versionJson
    )
    {
        var json = EnvelopeJson(versionJson);
        var codec = new JsonStateCodec<int>();
        var strict = new JsonStateCodec<int>(
            new JsonSerializerOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            }
        );

        var normalException = await Should.ThrowAsync<JsonException>(async () =>
            await ReadFromStreamAsync(codec, json)
        );
        codec.IsRecoverableReadException(normalException).ShouldBeTrue();

        var strictException = await Should.ThrowAsync<JsonException>(async () =>
            await ReadFromStreamAsync(strict, json)
        );
        strict.IsRecoverableReadException(strictException).ShouldBeTrue();
    }

    [Test]
    public void ValidEnvelopeVersion_RoundTripsAcrossMetadataPaths()
    {
        var sequence = EnvelopeSequence("3");
        var typed = new JsonStateCodec<int>();
        var dynamic = new JsonStateCodec();
        var expected = new StateSchemaMetadata("x", 3);

        typed.ReadSchemaMetadata(sequence).ShouldBe(expected);
        dynamic.ReadSchemaMetadata(sequence).ShouldBe(expected);

        var decoded = typed.DeserializeWithMetadata(sequence, default);
        decoded.Value.ShouldBe(42);
        decoded.Schema.ShouldBe(expected);
    }

    [Test]
    public async Task StrictReader_RecoversFromValidBackup()
    {
        var resource = new BackupStubResource(
            EnvelopeBytes("\"bad\""),
            EnvelopeBytes("2")
        );
        var codec = new JsonStateCodec<int>(
            new JsonSerializerOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            }
        );
        var reader = new SerializedStateReader<int>(resource, codec);

        var result = await reader.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Value.ShouldBe(42);
        result.Schema.ShouldBe(new StateSchemaMetadata("x", 2));
        resource.RecoveryAttempts.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task StrictReader_SurfacesRecoverableWhenBackupAlsoInvalid()
    {
        var resource = new BackupStubResource(EnvelopeBytes("\"bad\""), EnvelopeBytes("null"));
        var codec = new JsonStateCodec<int>(
            new JsonSerializerOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            }
        );
        var reader = new SerializedStateReader<int>(resource, codec);

        var exception = await Should.ThrowAsync<JsonException>(async () =>
            await reader.ReadAsync()
        );

        codec.IsRecoverableReadException(exception).ShouldBeTrue();
        resource.RecoveryAttempts.ShouldBeGreaterThanOrEqualTo(1);
    }

    private static string EnvelopeJson(string versionJson) =>
        "{\"$configlue\":{\"id\":\"x\",\"version\":" + versionJson + "},\"$value\":42}";

    private static ReadOnlySequence<byte> EnvelopeSequence(string versionJson) =>
        new(EnvelopeBytes(versionJson));

    private static byte[] EnvelopeBytes(string versionJson) =>
        Encoding.UTF8.GetBytes(EnvelopeJson(versionJson));

    private static async Task<StateReadResult<int>> ReadFromStreamAsync(
        JsonStateCodec<int> codec,
        string json
    )
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var pipe = PipeReader.Create(stream);
        try
        {
            return await codec.DeserializeAsync(pipe, default, null);
        }
        finally
        {
            await pipe.CompleteAsync();
        }
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
}
