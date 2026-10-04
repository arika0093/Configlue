using System.Buffers;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Configlue.CompilerServices;
using Configlue.Provider.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using SparseFragments.JsonPatch;

namespace Configlue.Hosting.AspNetCore;

/// <summary>Maps the typed Configlue State HTTP protocol to ASP.NET Core endpoints.</summary>
/// <remarks>
/// <para>Contract (v1):</para>
/// <list type="bullet">
/// <item><description><c>GET pattern</c> returns the current effective state as generated fragment JSON with a state ETag.</description></item>
/// <item><description><c>PUT pattern</c> replaces the caller's desired effective state through Core edit sessions.</description></item>
/// <item><description><c>PATCH pattern</c> applies an RFC 6902 <c>application/json-patch+json</c> document against the canonical effective representation. A strong effective-state <c>If-Match</c> ETag is required.</description></item>
/// <item><description><c>GET pattern/events</c> emits <c>event: changed</c> SSE invalidations with the state ETag as the event ID.</description></item>
/// </list>
/// <para>
/// GET, PUT, and PATCH responses advertise <c>Accept-Patch: application/json-patch+json</c>.
/// PATCH paths address the exact JSON shape returned by GET: all three verbs share the
/// generated fragment converter, naming policy, null handling, and ETag canonicalization.
/// </para>
/// <para>
/// The patched document is canonicalized through the generated model before commit: the
/// sparse RFC 6902 result is converted to <c>TModel</c> with normal model/default semantics
/// and committed through a Core edit session, so a removed fixed-schema property may reappear
/// on the next GET with its model/default value. Removal at the document level never means
/// "remove this source's contribution".
/// </para>
/// <para>Status mapping:</para>
/// <list type="table">
/// <listheader><term>Condition</term><term>HTTP status</term></listheader>
/// <item><term>Success</term><term>200 OK (304 Not Modified when If-None-Match matches on GET)</term></item>
/// <item><term>Malformed JSON</term><term>400 Bad Request</term></item>
/// <item><term>Malformed JSON Patch / JSON Pointer</term><term>400 Bad Request</term></item>
/// <item><term>Inapplicable patch operation, failed test</term><term>409 Conflict</term></item>
/// <item><term>Missing If-Match on PATCH</term><term>428 Precondition Required</term></item>
/// <item><term>Validation failure</term><term>422 Unprocessable Entity</term></item>
/// <item><term>Stale If-Match</term><term>412 Precondition Failed</term></item>
/// <item><term>Core write conflict</term><term>409 Conflict</term></item>
/// <item><term>Non-atomic multi-resource write plan</term><term>409 Conflict before any write</term></item>
/// <item><term>Authorization</term><term>Normal ASP.NET Core 401/403 behavior</term></item>
/// <item><term>Unexpected failure</term><term>ProblemDetails 5xx</term></item>
/// </list>
/// <para>
/// The state ETag is the lowercase hex SHA-256 of the canonical generated-fragment JSON bytes.
/// It answers "is the effective configuration I edited still current?" while Core's
/// source/resource-level concurrency checks continue underneath.
/// </para>
/// </remarks>
public static class ConfiglueStateEndpointRouteBuilderExtensions
{
    /// <summary>Maps typed GET/PUT/SSE state endpoints for <typeparamref name="TModel"/>.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="pattern">An absolute route pattern such as <c>/api/settings</c>.</param>
    /// <param name="options">Which endpoints to map and their relative paths.</param>
    /// <returns>The route group containing the mapped endpoints.</returns>
    public static RouteGroupBuilder MapConfiglueState<TModel>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        ConfiglueStateEndpointOptions? options = null
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var validated = ValidateOptions(options ?? new ConfiglueStateEndpointOptions());
        ValidatePattern(pattern);

        // Touch the descriptor eagerly so a missing generated registration fails at map time.
        _ = ConfiglueModelDescriptor<TModel>.Current;

        var group = endpoints.MapGroup(pattern);
        if (validated.MapRead)
        {
            group.MapGet(
                ToRoutePath(validated.ReadPath),
                (HttpContext context) => HandleGetAsync<TModel>(context, validated)
            );
        }

        if (validated.MapWrite)
        {
            group.MapPut(
                ToRoutePath(validated.WritePath),
                (HttpContext context) => HandlePutAsync<TModel>(context, validated)
            );
        }

        if (validated.MapPatch)
        {
            group.MapPatch(
                ToRoutePath(validated.WritePath),
                (HttpContext context) => HandlePatchAsync<TModel>(context, validated)
            );
        }

        foreach (var statePath in CollectStatePaths(validated))
        {
            var allowed = string.Join(
                ", ",
                CollectAllowedMethods(validated, statePath).Append("OPTIONS")
            );
            var advertisePatch = validated.MapPatch;
            group.MapMethods(
                statePath,
                ["OPTIONS"],
                (HttpContext context) =>
                {
                    context.Response.Headers["Allow"] = allowed;
                    if (advertisePatch)
                    {
                        SetAcceptPatch(context);
                    }

                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return Task.CompletedTask;
                }
            );
        }

        if (validated.MapEvents)
        {
            group.MapGet(
                ToRoutePath(validated.EventsPath),
                (HttpContext context) => HandleEventsAsync<TModel>(context, validated)
            );
        }

        return group;
    }

    private static string ToRoutePath(string relativePath) =>
        relativePath.Length == 0 ? "/" : relativePath;

    private static IEnumerable<string> CollectStatePaths(ValidatedOptions options)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (options.MapRead)
        {
            paths.Add(ToRoutePath(options.ReadPath));
        }

        if (options.MapWrite || options.MapPatch)
        {
            paths.Add(ToRoutePath(options.WritePath));
        }

        return paths;
    }

    private static IEnumerable<string> CollectAllowedMethods(
        ValidatedOptions options,
        string statePath
    )
    {
        if (
            options.MapRead
            && string.Equals(ToRoutePath(options.ReadPath), statePath, StringComparison.Ordinal)
        )
        {
            yield return "GET";
        }

        if (
            options.MapWrite
            && string.Equals(ToRoutePath(options.WritePath), statePath, StringComparison.Ordinal)
        )
        {
            yield return "PUT";
        }

        if (
            options.MapPatch
            && string.Equals(ToRoutePath(options.WritePath), statePath, StringComparison.Ordinal)
        )
        {
            yield return "PATCH";
        }
    }

    private static void SetAcceptPatch(HttpContext context) =>
        context.Response.Headers["Accept-Patch"] = "application/json-patch+json";

    private static async Task HandleGetAsync<TModel>(HttpContext context, ValidatedOptions options)
        where TModel : IConfiglueFacadeModel<TModel>
    {
        SetAcceptPatch(context);
        var descriptor = ConfiglueModelDescriptor<TModel>.Current;
        TModel value;
        try
        {
            var state = context.RequestServices.GetRequiredService<IReadOnlyState<TModel>>();
            value = await state.GetValueAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (ConfiglueValidationException exception)
        {
            await WriteValidationProblemAsync(context, exception).ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State read failed.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }

        byte[] json;
        try
        {
            json = EncodeModel(descriptor, value, options.SerializerOptions);
        }
        catch (Exception exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State serialization failed.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }

        var etag = ComputeEtagHex(json);
        context.Response.Headers.ETag = FormatEtag(etag);
        if (IfNoneMatchMatches(context.Request, etag))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = json.Length;
        if (json.Length > 0)
        {
            await context
                .Response.Body.WriteAsync(json, context.RequestAborted)
                .ConfigureAwait(false);
        }
    }

    private static async Task HandlePutAsync<TModel>(HttpContext context, ValidatedOptions options)
        where TModel : IConfiglueFacadeModel<TModel>
    {
        SetAcceptPatch(context);
        if (!HasJsonContentType(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }

        if (!TryReadIfMatch(context.Request, out var ifMatch, out var preconditionMalformed))
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Invalid precondition.",
                    "The If-Match header must be a single strong Configlue state ETag or '*', and must not be combined with If-None-Match."
                )
                .ConfigureAwait(false);
            return;
        }

        if (preconditionMalformed)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Invalid precondition.",
                    "The If-Match header must be a single strong Configlue state ETag or '*'."
                )
                .ConfigureAwait(false);
            return;
        }

        if (context.Request.Headers.ContainsKey("If-None-Match"))
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Invalid precondition.",
                    "PUT supports If-Match only; If-None-Match is not supported for state writes."
                )
                .ConfigureAwait(false);
            return;
        }

        byte[]? body = await ReadRequestBodyAsync(context, options).ConfigureAwait(false);
        if (body is null)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var descriptor = ConfiglueModelDescriptor<TModel>.Current;
        TModel incoming;
        try
        {
            incoming = DecodeModel<TModel>(descriptor, body, options.SerializerOptions);
        }
        catch (JsonException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Malformed state payload.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Malformed state payload.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }

        IReadOnlyState<TModel> readState;
        IConfiglueEditSessions<TModel> editSessions;
        try
        {
            readState = context.RequestServices.GetRequiredService<IReadOnlyState<TModel>>();
            editSessions = context.RequestServices.GetRequiredService<
                IConfiglueEditSessions<TModel>
            >();
        }
        catch (InvalidOperationException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State services are not registered.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }

        string currentEtag;
        try
        {
            var current = await readState
                .GetValueAsync(context.RequestAborted)
                .ConfigureAwait(false);
            currentEtag = ComputeEtagHex(
                EncodeModel(descriptor, current, options.SerializerOptions)
            );
        }
        catch (ConfiglueValidationException exception)
        {
            await WriteValidationProblemAsync(context, exception).ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State read failed.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }

        if (
            ifMatch is { } expected
            && !string.Equals(expected, "*", StringComparison.Ordinal)
            && !string.Equals(expected, currentEtag, StringComparison.Ordinal)
        )
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status412PreconditionFailed,
                    "The effective state changed after it was read.",
                    "The If-Match ETag does not match the current effective state."
                )
                .ConfigureAwait(false);
            return;
        }

        TModel committed;
        try
        {
            using var session = await editSessions
                .OpenEditSessionAsync(context.RequestAborted)
                .ConfigureAwait(false);
            session.Value = incoming;
            await session.CommitAsync(context.RequestAborted).ConfigureAwait(false);
            committed = session.Value;
        }
        catch (ConfiglueValidationException exception)
        {
            await WriteValidationProblemAsync(context, exception).ConfigureAwait(false);
            return;
        }
        catch (StateConflictException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "The state write conflicts with a concurrent change.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }
        catch (StateMultiWriteException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "A multi-source write failed after partial completion.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State write failed.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }

        byte[] responseJson;
        try
        {
            responseJson = EncodeModel(descriptor, committed, options.SerializerOptions);
        }
        catch (Exception exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State serialization failed.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }

        var newEtag = ComputeEtagHex(responseJson);
        context.Response.Headers.ETag = FormatEtag(newEtag);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = responseJson.Length;
        if (responseJson.Length > 0)
        {
            await context
                .Response.Body.WriteAsync(responseJson, context.RequestAborted)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Reads the request body, enforcing the configured size limit.</summary>
    /// <returns>The body bytes, or null when the body exceeds the configured limit.</returns>
    private static async Task<byte[]?> ReadRequestBodyAsync(
        HttpContext context,
        ValidatedOptions options
    )
    {
        if (
            options.MaximumRequestBodySize is { } maximumSize
            && context.Request.ContentLength is { } contentLength
            && contentLength > maximumSize
        )
        {
            return null;
        }

        using var content = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                var bytesRead = await context
                    .Request.Body.ReadAsync(buffer, context.RequestAborted)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                if (
                    options.MaximumRequestBodySize is { } limit
                    && content.Length > limit - bytesRead
                )
                {
                    return null;
                }

                await content
                    .WriteAsync(buffer.AsMemory(0, bytesRead), context.RequestAborted)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        return content.ToArray();
    }

    private static async Task HandlePatchAsync<TModel>(
        HttpContext context,
        ValidatedOptions options
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        SetAcceptPatch(context);
        if (!HasJsonPatchContentType(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }

        if (context.Request.Headers.IfMatch.Count == 0)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status428PreconditionRequired,
                    "Precondition required.",
                    "PATCH requires a strong effective-state If-Match ETag. Read the current state with GET first."
                )
                .ConfigureAwait(false);
            return;
        }

        if (!TryReadIfMatch(context.Request, out var ifMatch, out var preconditionMalformed))
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Invalid precondition.",
                    "The If-Match header must be a single strong Configlue state ETag, and must not be combined with If-None-Match."
                )
                .ConfigureAwait(false);
            return;
        }

        if (
            preconditionMalformed
            || string.Equals(ifMatch, "*", StringComparison.Ordinal)
            || context.Request.Headers.ContainsKey("If-None-Match")
        )
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Invalid precondition.",
                    "PATCH requires a single strong Configlue state ETag. Weak ETags, '*' and If-None-Match are not accepted as a patch baseline."
                )
                .ConfigureAwait(false);
            return;
        }

        byte[]? body = await ReadRequestBodyAsync(context, options).ConfigureAwait(false);
        if (body is null)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        JsonPatchDocument document;
        try
        {
            document = SparseJsonPatch.Parse(body);
        }
        catch (JsonPatchException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Malformed JSON Patch document.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }

        IReadOnlyState<TModel> readState;
        IConfiglueEditSessions<TModel> editSessions;
        try
        {
            readState = context.RequestServices.GetRequiredService<IReadOnlyState<TModel>>();
            editSessions = context.RequestServices.GetRequiredService<
                IConfiglueEditSessions<TModel>
            >();
        }
        catch (InvalidOperationException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State services are not registered.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }

        TModel current;
        try
        {
            current = await readState.GetValueAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (ConfiglueValidationException exception)
        {
            await WriteValidationProblemAsync(context, exception).ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State read failed.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }

        var descriptor = ConfiglueModelDescriptor<TModel>.Current;
        byte[] canonicalJson;
        try
        {
            canonicalJson = EncodeModel(descriptor, current, options.SerializerOptions);
        }
        catch (Exception exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State serialization failed.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }

        var currentEtag = ComputeEtagHex(canonicalJson);
        if (!string.Equals(ifMatch, currentEtag, StringComparison.Ordinal))
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status412PreconditionFailed,
                    "The effective state changed after it was read.",
                    "The If-Match ETag does not match the current effective state."
                )
                .ConfigureAwait(false);
            return;
        }

        // PATCH paths address the exact GET JSON shape: the baseline is the canonical
        // fragment JSON and the same serializer options (naming, null handling) apply.
        JsonNode? baselineNode;
        try
        {
            baselineNode = JsonNode.Parse(canonicalJson);
        }
        catch (Exception exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State serialization failed.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }

        var effectiveOptions = ConfiglueFragmentJson.CreateOptions(options.SerializerOptions);
        var comparison = effectiveOptions.PropertyNameCaseInsensitive
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        JsonPatchEngine.ApplyResult applied;
        try
        {
            applied = SparseJsonPatch.Apply(baselineNode, false, document, comparison);
        }
        catch (JsonPatchException exception)
            when (exception.Kind
                    is JsonPatchErrorKind.MalformedDocument
                        or JsonPatchErrorKind.MalformedPointer
                        or JsonPatchErrorKind.UnknownOperation
            )
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Malformed JSON Patch document.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }
        catch (JsonPatchException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "The JSON Patch cannot be applied to the current state.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }

        if (
            applied is { IsAbsent: false, Node: not null }
            && JsonNode.DeepEquals(applied.Node, baselineNode)
        )
        {
            // No-op (including the empty document): validate If-Match, then succeed
            // without physical writes and without emitting a state-change event.
            await WriteStateResponseAsync(context, descriptor, current, options)
                .ConfigureAwait(false);
            return;
        }

        byte[] patchedJson;
        if (applied is { IsAbsent: true } or { Node: null })
        {
            // A removed document root, or a root replaced with JSON null, cannot remain a
            // typed state object. Normalize through the model defaults, exactly like a
            // removed fixed-schema property reappearing with its default on the next GET.
            patchedJson = "{}"u8.ToArray();
        }
        else
        {
            patchedJson = Encoding.UTF8.GetBytes(applied.Node.ToJsonString());
        }

        object patchedFragment;
        try
        {
            var strict = ConfiglueFragmentJson.CreateOptions(options.SerializerOptions);
            strict.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            patchedFragment = ConfiglueFragmentJson.Deserialize(
                descriptor.FragmentType,
                patchedJson,
                strict
            );
        }
        catch (JsonException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "The JSON Patch cannot be applied to the current state.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State services are not registered.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }

        TModel desired;
        try
        {
            desired = (TModel)descriptor.FromFragmentBoxed(patchedFragment);
        }
        catch (Exception exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State normalization failed.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }

        // RFC 5789 atomicity gate: determine executability before the first physical write.
        if (editSessions is not IConfiglueWritePreview<TModel> preview)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State services are not registered.",
                    "The state does not expose an atomic PATCH write preview."
                )
                .ConfigureAwait(false);
            return;
        }

        StateWritePreview? writePreview;
        try
        {
            writePreview = await preview
                .PreviewWriteAsync(desired, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (ConfiglueValidationException exception)
        {
            await WriteValidationProblemAsync(context, exception).ConfigureAwait(false);
            return;
        }
        catch (StateConflictException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "The state write conflicts with a concurrent change.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }
        catch (NotSupportedException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "The requested State PATCH spans multiple non-transactional write resources.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State write preview failed.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }

        if (writePreview.IsEmpty)
        {
            await WriteStateResponseAsync(context, descriptor, current, options)
                .ConfigureAwait(false);
            return;
        }

        if (!writePreview.IsAtomic)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "The requested State PATCH spans multiple non-transactional write resources.",
                    $"The patch would require {writePreview.PhysicalWriteCount} independent physical writes with no transactional batch guarantee, so it was rejected before writing anything."
                )
                .ConfigureAwait(false);
            return;
        }

        TModel committed;
        try
        {
            using var session = await editSessions
                .OpenEditSessionAsync(context.RequestAborted)
                .ConfigureAwait(false);
            session.Value = desired;
            await session.CommitAsync(context.RequestAborted).ConfigureAwait(false);
            committed = session.Value;
        }
        catch (ConfiglueValidationException exception)
        {
            await WriteValidationProblemAsync(context, exception).ConfigureAwait(false);
            return;
        }
        catch (StateConflictException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "The state write conflicts with a concurrent change.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }
        catch (StateMultiWriteException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "A multi-source write failed after partial completion.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State write failed.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }

        await WriteStateResponseAsync(context, descriptor, committed, options)
            .ConfigureAwait(false);
    }

    private static async Task WriteStateResponseAsync<TModel>(
        HttpContext context,
        ConfiglueModelDescriptor<TModel> descriptor,
        TModel value,
        ValidatedOptions options
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        byte[] responseJson;
        try
        {
            responseJson = EncodeModel(descriptor, value, options.SerializerOptions);
        }
        catch (Exception exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State serialization failed.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }

        var newEtag = ComputeEtagHex(responseJson);
        context.Response.Headers.ETag = FormatEtag(newEtag);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = responseJson.Length;
        if (responseJson.Length > 0)
        {
            await context
                .Response.Body.WriteAsync(responseJson, context.RequestAborted)
                .ConfigureAwait(false);
        }
    }

    private static async Task HandleEventsAsync<TModel>(
        HttpContext context,
        ValidatedOptions options
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        var descriptor = ConfiglueModelDescriptor<TModel>.Current;
        IReadOnlyState<TModel> state;
        try
        {
            state = context.RequestServices.GetRequiredService<IReadOnlyState<TModel>>();
        }
        catch (InvalidOperationException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State services are not registered.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }

        string currentEtag;
        try
        {
            var current = await state.GetValueAsync(context.RequestAborted).ConfigureAwait(false);
            currentEtag = ComputeEtagHex(
                EncodeModel(descriptor, current, options.SerializerOptions)
            );
        }
        catch (ConfiglueValidationException exception)
        {
            await WriteValidationProblemAsync(context, exception).ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException exception)
        {
            await WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State read failed.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }

        var lastEventIds = context.Request.Headers["Last-Event-ID"];
        if (
            lastEventIds.Count == 1
            && TryExtractHex(FormatEtag(lastEventIds[0] ?? string.Empty), out var lastEventId)
        )
        {
            currentEtag = lastEventId;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        try
        {
            context.Response.Headers.Connection = "keep-alive";
        }
        catch (InvalidOperationException)
        {
            // HTTP/2 forbids the Connection header; streaming works without it.
        }

        context.Response.Headers["X-Accel-Buffering"] = "no";
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);

        var cancellation = context.RequestAborted;
        object gate = new();
        string? pendingEtag = null;
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        IDisposable subscription = state.OnChange(newValue =>
        {
            try
            {
                var fragment = descriptor.ToFragmentBoxed(newValue);
                var bytes = ConfiglueFragmentJson.SerializeToCanonicalBytes(
                    fragment,
                    descriptor.FragmentType,
                    options.SerializerOptions
                );
                var etag = ComputeEtagHex(bytes);
                lock (gate)
                {
                    if (string.Equals(etag, currentEtag, StringComparison.Ordinal))
                    {
                        return;
                    }

                    pendingEtag = etag;
                    signal.TrySetResult();
                }
            }
            catch (Exception exception)
            {
                // Serialization failures must not break the SSE subscription; the next
                // successful change notification will still converge the client.
                System.Diagnostics.Debug.WriteLine(exception);
            }
        });

        try
        {
            // Subscribe before the convergence read so changes during SSE startup
            // are observed even when they occurred after the client's last GET.
            var current = await state.GetValueAsync(cancellation).ConfigureAwait(false);
            var convergedEtag = ComputeEtagHex(
                EncodeModel(descriptor, current, options.SerializerOptions)
            );
            lock (gate)
            {
                // A notification received during the read already supplies a pending
                // invalidation; avoid replacing it with a possibly older snapshot.
                if (
                    pendingEtag is null
                    && !string.Equals(convergedEtag, currentEtag, StringComparison.Ordinal)
                )
                {
                    pendingEtag = convergedEtag;
                    signal.TrySetResult();
                }
            }

            while (!cancellation.IsCancellationRequested)
            {
                Task signalTask;
                lock (gate)
                {
                    signalTask = signal.Task;
                }

                try
                {
                    await signalTask.WaitAsync(cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                string etagToSend;
                lock (gate)
                {
                    etagToSend = pendingEtag ?? currentEtag;
                    pendingEtag = null;
                    if (signal.Task.IsCompleted)
                    {
                        signal = new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously
                        );
                    }
                }

                if (string.Equals(etagToSend, currentEtag, StringComparison.Ordinal))
                {
                    continue;
                }

                currentEtag = etagToSend;
                var payload = $"event: changed\nid: {etagToSend}\ndata: {{}}\n\n";
                var bytes = Encoding.UTF8.GetBytes(payload);
                try
                {
                    await context
                        .Response.Body.WriteAsync(bytes, cancellation)
                        .ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }
            }
        }
        finally
        {
            subscription.Dispose();
        }
    }

    internal static byte[] EncodeModel<TModel>(
        ConfiglueModelDescriptor<TModel> descriptor,
        TModel value,
        JsonSerializerOptions? options
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(value);
        var fragment = descriptor.ToFragmentBoxed(value);
        return ConfiglueFragmentJson.SerializeToCanonicalBytes(
            fragment,
            descriptor.FragmentType,
            options
        );
    }

    internal static TModel DecodeModel<TModel>(
        ConfiglueModelDescriptor<TModel> descriptor,
        byte[] json,
        JsonSerializerOptions? options
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(json);
        var fragment = ConfiglueFragmentJson.Deserialize(descriptor.FragmentType, json, options);

        return (TModel)descriptor.FromFragmentBoxed(fragment);
    }

    internal static string ComputeEtagHex(byte[] canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);
        return Convert.ToHexString(SHA256.HashData(canonicalJson)).ToLowerInvariant();
    }

    private static string FormatEtag(string hex) => $"\"{hex}\"";

    private static bool HasJsonContentType(HttpRequest request)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType))
        {
            return false;
        }

        return string.Equals(
            contentType.MediaType,
            "application/json",
            StringComparison.OrdinalIgnoreCase
        );
    }

    private static bool HasJsonPatchContentType(HttpRequest request)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType))
        {
            return false;
        }

        return string.Equals(
            contentType.MediaType,
            "application/json-patch+json",
            StringComparison.OrdinalIgnoreCase
        );
    }

    private static bool TryReadIfMatch(HttpRequest request, out string? etag, out bool malformed)
    {
        etag = null;
        malformed = false;
        var values = request.Headers.IfMatch;
        if (values.Count == 0)
        {
            return true;
        }

        if (values.Count != 1)
        {
            return false;
        }

        var raw = values[0]?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        if (raw == "*")
        {
            etag = "*";
            return true;
        }

        if (raw.Contains(','))
        {
            return false;
        }

        if (!EntityTagHeaderValue.TryParse(raw, out var parsed) || parsed is null || parsed.IsWeak)
        {
            malformed = true;
            return true;
        }

        if (!TryExtractHex(parsed.Tag, out var hex))
        {
            malformed = true;
            return true;
        }

        etag = hex;
        return true;
    }

    private static bool IfNoneMatchMatches(HttpRequest request, string currentHex)
    {
        var values = request.Headers.IfNoneMatch;
        if (values.Count == 0)
        {
            return false;
        }

        foreach (var rawValue in values)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            var value = rawValue.Trim();
            if (value == "*")
            {
                return true;
            }

            foreach (var part in value.Split(','))
            {
                var candidate = part.Trim();
                if (candidate.Length == 0)
                {
                    continue;
                }

                if (!EntityTagHeaderValue.TryParse(candidate, out var parsed) || parsed is null)
                {
                    continue;
                }

                if (
                    TryExtractHex(parsed.Tag, out var hex)
                    && string.Equals(hex, currentHex, StringComparison.Ordinal)
                )
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryExtractHex(string tag, out string hex)
    {
        hex = "";
        if (tag.Length < 2 || !tag.StartsWith('"') || !tag.EndsWith('"'))
        {
            return false;
        }

        var inner = tag[1..^1];
        if (inner.Length != 64)
        {
            return false;
        }

        if (inner.Any(static c => !Uri.IsHexDigit(c)))
        {
            return false;
        }

        hex = inner.ToLowerInvariant();
        return true;
    }

    private static Task WriteValidationProblemAsync(
        HttpContext context,
        ConfiglueValidationException exception
    )
    {
        return WriteProblemAsync(
            context,
            StatusCodes.Status422UnprocessableEntity,
            "State validation failed.",
            string.Join("; ", exception.Failures)
        );
    }

    private static async Task WriteProblemAsync(
        HttpContext context,
        int statusCode,
        string title,
        string detail
    )
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        var problem = new
        {
            title,
            status = statusCode,
            detail,
        };
        await JsonSerializer
            .SerializeAsync(
                context.Response.Body,
                problem,
                new JsonSerializerOptions(),
                context.RequestAborted
            )
            .ConfigureAwait(false);
    }

    private sealed record ValidatedOptions(
        bool MapRead,
        bool MapWrite,
        bool MapPatch,
        bool MapEvents,
        string ReadPath,
        string WritePath,
        string EventsPath,
        JsonSerializerOptions? SerializerOptions,
        long? MaximumRequestBodySize
    );

    private static ValidatedOptions ValidateOptions(ConfiglueStateEndpointOptions options)
    {
        if (!options.MapRead && !options.MapWrite && !options.MapPatch && !options.MapEvents)
        {
            throw new ArgumentException("At least one endpoint must be mapped.", nameof(options));
        }

        ValidateStatePath(options.ReadPath, nameof(options.ReadPath), allowEmpty: true);
        ValidateStatePath(options.WritePath, nameof(options.WritePath), allowEmpty: true);
        ValidateStatePath(options.EventsPath, nameof(options.EventsPath), allowEmpty: false);
        // GET and PUT may share the pattern root (different methods), but the SSE GET
        // endpoint must not collide with the state GET endpoint.
        if (
            options.MapRead
            && options.MapEvents
            && string.Equals(
                NormalizeForComparison(options.ReadPath),
                NormalizeForComparison(options.EventsPath),
                StringComparison.Ordinal
            )
        )
        {
            throw new ArgumentException(
                "The events endpoint must differ from the read endpoint.",
                nameof(options)
            );
        }

        if (options.MaximumRequestBodySize is <= 0 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaximumRequestBodySize must be between one and Int32.MaxValue bytes or null."
            );
        }

        return new ValidatedOptions(
            options.MapRead,
            options.MapWrite,
            options.MapPatch,
            options.MapEvents,
            options.ReadPath,
            options.WritePath,
            options.EventsPath,
            options.SerializerOptions,
            options.MaximumRequestBodySize
        );
    }

    private static string NormalizeForComparison(string path) => path.Trim('/').ToLowerInvariant();

    private static void ValidateStatePath(string path, string parameterName, bool allowEmpty)
    {
        if (path.Length == 0)
        {
            if (!allowEmpty)
            {
                throw new ArgumentException(
                    "Endpoint paths must be non-empty relative paths without a query, fragment, or root escape.",
                    parameterName
                );
            }

            return;
        }

        if (
            path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains('?')
            || path.Contains('#')
            || path.Contains('\\')
            || !Uri.TryCreate(path, UriKind.Relative, out var relativePath)
            || relativePath.IsAbsoluteUri
        )
        {
            throw new ArgumentException(
                "Endpoint paths must be non-empty relative paths without a query, fragment, or root escape.",
                parameterName
            );
        }

        foreach (var segment in path.Split('/'))
        {
            var decodedSegment = Uri.UnescapeDataString(segment);
            if (
                decodedSegment is "." or ".."
                || decodedSegment.Contains('/')
                || decodedSegment.Contains('\\')
            )
            {
                throw new ArgumentException(
                    "Endpoint paths must remain under the route root.",
                    parameterName
                );
            }
        }
    }

    private static void ValidatePattern(string pattern)
    {
        if (
            !pattern.StartsWith("/", StringComparison.Ordinal)
            || pattern.StartsWith("//", StringComparison.Ordinal)
            || pattern.Contains('?')
            || pattern.Contains('#')
            || pattern.Contains('\\')
        )
        {
            throw new ArgumentException(
                "The route pattern must be an absolute route path without a query or fragment.",
                nameof(pattern)
            );
        }

        try
        {
            _ = RoutePatternFactory.Parse(pattern);
        }
        catch (RoutePatternException exception)
        {
            throw new ArgumentException(
                "The route pattern is not a valid route pattern.",
                nameof(pattern),
                exception
            );
        }
    }
}
