using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Configlue.Provider.Json;
using Configlue.State;
using SparseFragments.JsonPatch;

namespace Configlue.Source.Http;

/// <summary>Write strategy: PATCH-vs-PUT decisions over a trustworthy cached baseline.</summary>
internal sealed class HttpStateWriteStrategy<TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly HttpStateTransport<TFragment> _transport;
    private readonly HttpStateBaselineCache _baseline;
    private readonly JsonSerializerOptions? _serializerOptions;
    private readonly bool _writable;

    public HttpStateWriteStrategy(
        HttpStateTransport<TFragment> transport,
        HttpStateBaselineCache baseline,
        JsonSerializerOptions? serializerOptions,
        bool writable
    )
    {
        _transport = transport;
        _baseline = baseline;
        _serializerOptions = serializerOptions;
        _writable = writable;
    }

    public async ValueTask<HttpStatePatchResult<TFragment>> PatchAsync(
        JsonPatchDocument patch,
        string etag,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(patch);
        var revision = HttpStateProtocol.NormalizeEtagArgument(etag);
        var body = SparseJsonPatch.Serialize(patch);
        HttpTransportPatchResult<TFragment> result;
        try
        {
            result = await _transport
                .PatchAsync(body, revision, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpStateStaleException)
        {
            _baseline.ClearBaselineJson();
            throw;
        }

        if (
            result.Content is { Length: > 0 } content
            && result is { Value: not null, Revision: not null }
        )
        {
            _baseline.SetBaseline(content, result.Revision);
            return new HttpStatePatchResult<TFragment>(result.Value, result.Revision);
        }

        if (result.Revision is not null && result.Value is null && result.Content is null)
        {
            // 204 No Content: keep the new revision without payload bytes.
            _baseline.SetRevision(result.Revision);
            return new HttpStatePatchResult<TFragment>(default, result.Revision);
        }

        _baseline.SetRevision(result.Revision);
        throw new HttpStateException(
            "The State HTTP PATCH endpoint returned an empty state payload."
        );
    }

    public async ValueTask<StateWriteResult> WriteAsync(
        StateWriteRequest<TFragment> request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_writable)
        {
            throw new InvalidOperationException("This State HTTP source is read-only.");
        }

        var body = SerializeValue(request.Value);

        // PATCH is an optimization over a trustworthy baseline: when the write carries a
        // revision matching the cached baseline, derive a JSON Patch from baseline to
        // desired state and send it with If-Match. Without a baseline, or when the server
        // does not map PATCH, fall back to the PUT path rather than inventing a patch
        // against an unknown base or forcing a preliminary GET.
        if (_baseline.TryGetPatchBaseline(request, out var patchRevision, out var baselineJson))
        {
            try
            {
                return await SendPatchWriteAsync(
                        baselineJson,
                        body,
                        patchRevision,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch (PatchUnsupportedException)
            {
                // The server does not map PATCH: fall through to PUT.
            }
        }

        return await SendPutAsync(body, request, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<StateWriteResult> SendPatchWriteAsync(
        byte[] baselineJson,
        byte[] desiredBody,
        string revision,
        CancellationToken cancellationToken
    )
    {
        JsonNode? before;
        JsonNode? after;
        try
        {
            before = JsonNode.Parse(Encoding.UTF8.GetString(baselineJson));
            after = JsonNode.Parse(Encoding.UTF8.GetString(desiredBody));
        }
        catch (Exception exception)
        {
            throw new HttpStateRequestException(
                "Failed to derive a JSON Patch baseline: " + exception.Message
            );
        }

        var document = SparseJsonPatch.Diff(
            before,
            beforeIsAbsent: false,
            after,
            afterIsAbsent: false
        );
        var patchBody = SparseJsonPatch.Serialize(document);
        HttpTransportPatchResult<TFragment> result;
        try
        {
            result = await _transport
                .PatchAsync(patchBody, revision, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpStateStaleException)
        {
            _baseline.ClearBaselineJson();
            throw;
        }

        if (result is { Value: null, Content: not null })
        {
            // 200 OK without a parsable payload is a protocol error; keep the
            // revision update before surfacing it, matching historic behavior.
            _baseline.SetRevision(result.Revision);
            throw new HttpStateException(
                "The State HTTP PATCH endpoint returned an empty state payload."
            );
        }

        if (result.Content is { Length: > 0 } content && result.Revision is not null)
        {
            _baseline.SetBaseline(content, result.Revision);
        }
        else
        {
            _baseline.SetRevision(result.Revision);
        }

        return new StateWriteResult(result.Revision);
    }

    private async ValueTask<StateWriteResult> SendPutAsync(
        byte[] body,
        StateWriteRequest<TFragment> request,
        CancellationToken cancellationToken
    )
    {
        var result = await _transport
            .PutAsync(body, request, cancellationToken)
            .ConfigureAwait(false);
        _baseline.CachePutResult(
            result.Revision,
            result.Content,
            content => _transport.TryParseFragment(content, out _)
        );
        return new StateWriteResult(result.Revision);
    }

    private byte[] SerializeValue(TFragment value)
    {
        try
        {
            var converter = ConfiglueJsonFragmentRegistry<TFragment>.Converter;
            var options = ConfiglueFragmentJson.CreateOptions(_serializerOptions);
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                converter.Write(writer, value, options);
            }

            return buffer.WrittenMemory.ToArray();
        }
        catch (Exception exception)
        {
            throw new HttpStateRequestException(
                "Failed to serialize the state fragment: " + exception.Message
            );
        }
    }
}
