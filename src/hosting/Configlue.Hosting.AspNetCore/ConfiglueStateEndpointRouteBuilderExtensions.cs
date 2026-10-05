using System.Text.Json;
using Configlue.CompilerServices;
using Configlue.Provider.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

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
/// <para>
/// Request processing is decomposed into protocol stages (see
/// <c>StateEndpointPreconditions</c>, <c>StateEndpointBody</c>,
/// <c>StateEndpointCodec</c>, <c>StateEndpointPatch</c>, <c>StateEndpointWrite</c>,
/// <c>StateEndpointProblems</c>, <c>StateEndpointSse</c>). The handlers below only
/// orchestrate those stages; HTTP semantics live in the small internal helpers.
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
        var validated = ValidatedStateEndpointOptions.From(
            options ?? new ConfiglueStateEndpointOptions()
        );
        StateEndpointRoutes.ValidatePattern(pattern);

        // Touch the descriptor eagerly so a missing generated registration fails at map time.
        _ = ConfiglueModelDescriptor<TModel>.Current;

        var group = endpoints.MapGroup(pattern);
        if (validated.MapRead)
        {
            group.MapGet(
                StateEndpointRoutes.ToRoutePath(validated.ReadPath),
                (HttpContext context) => HandleGetAsync<TModel>(context, validated)
            );
        }

        if (validated.MapWrite)
        {
            group.MapPut(
                StateEndpointRoutes.ToRoutePath(validated.WritePath),
                (HttpContext context) => HandlePutAsync<TModel>(context, validated)
            );
        }

        if (validated.MapPatch)
        {
            group.MapPatch(
                StateEndpointRoutes.ToRoutePath(validated.WritePath),
                (HttpContext context) => HandlePatchAsync<TModel>(context, validated)
            );
        }

        foreach (var statePath in StateEndpointRoutes.CollectStatePaths(validated))
        {
            var allowed = string.Join(
                ", ",
                StateEndpointRoutes.CollectAllowedMethods(validated, statePath).Append("OPTIONS")
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
                        StateEndpointRoutes.SetAcceptPatch(context);
                    }

                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return Task.CompletedTask;
                }
            );
        }

        if (validated.MapEvents)
        {
            group.MapGet(
                StateEndpointRoutes.ToRoutePath(validated.EventsPath),
                (HttpContext context) => HandleEventsAsync<TModel>(context, validated)
            );
        }

        return group;
    }

    private static async Task HandleGetAsync<TModel>(
        HttpContext context,
        ValidatedStateEndpointOptions options
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        StateEndpointRoutes.SetAcceptPatch(context);
        var snapshot = await StateEndpointCodec
            .LoadCurrentSnapshotAsync<TModel>(context, options)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return;
        }

        context.Response.Headers.ETag = StateEndpointPreconditions.FormatEtag(snapshot.EtagHex);
        if (StateEndpointPreconditions.IfNoneMatchMatches(context.Request, snapshot.EtagHex))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = snapshot.CanonicalJson.Length;
        if (snapshot.CanonicalJson.Length > 0)
        {
            await context
                .Response.Body.WriteAsync(snapshot.CanonicalJson, context.RequestAborted)
                .ConfigureAwait(false);
        }
    }

    private static async Task HandlePutAsync<TModel>(
        HttpContext context,
        ValidatedStateEndpointOptions options
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        StateEndpointRoutes.SetAcceptPatch(context);
        if (!StateEndpointPreconditions.HasJsonContentType(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }

        var precondition = await StateEndpointPreconditions
            .RequirePutPreconditionAsync(context)
            .ConfigureAwait(false);
        if (precondition is null)
        {
            return;
        }

        var body = await StateEndpointBody.RequireBodyAsync(context, options).ConfigureAwait(false);
        if (body is null)
        {
            return;
        }

        var descriptor = ConfiglueModelDescriptor<TModel>.Current;
        TModel incoming;
        try
        {
            incoming = StateEndpointCodec.DecodeModel<TModel>(
                descriptor,
                body,
                options.SerializerOptions
            );
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            await StateEndpointProblems
                .WriteDecodeProblemAsync(context, exception)
                .ConfigureAwait(false);
            return;
        }

        var services = await StateEndpointWrite
            .ResolveServicesAsync<TModel>(context)
            .ConfigureAwait(false);
        if (services is null)
        {
            return;
        }

        var snapshot = await StateEndpointCodec
            .LoadCurrentSnapshotAsync<TModel>(context, options)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return;
        }

        if (
            !await StateEndpointCodec
                .RequireEtagMatchAsync(context, precondition.IfMatch, snapshot.EtagHex)
                .ConfigureAwait(false)
        )
        {
            return;
        }

        var committed = await StateEndpointWrite
            .CommitDesiredAsync(context, services.EditSessions, incoming)
            .ConfigureAwait(false);
        if (committed is null)
        {
            return;
        }

        await StateEndpointCodec
            .WriteStateResponseAsync(context, descriptor, committed, options)
            .ConfigureAwait(false);
    }

    private static async Task HandlePatchAsync<TModel>(
        HttpContext context,
        ValidatedStateEndpointOptions options
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        StateEndpointRoutes.SetAcceptPatch(context);
        if (!StateEndpointPreconditions.HasJsonPatchContentType(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }

        var precondition = await StateEndpointPreconditions
            .RequirePatchPreconditionAsync(context)
            .ConfigureAwait(false);
        if (precondition is null)
        {
            return;
        }

        var body = await StateEndpointBody.RequireBodyAsync(context, options).ConfigureAwait(false);
        if (body is null)
        {
            return;
        }

        var document = await StateEndpointPatch
            .ParseDocumentAsync(context, body)
            .ConfigureAwait(false);
        if (document is null)
        {
            return;
        }

        var services = await StateEndpointWrite
            .ResolveServicesAsync<TModel>(context)
            .ConfigureAwait(false);
        if (services is null)
        {
            return;
        }

        var snapshot = await StateEndpointCodec
            .LoadCurrentSnapshotAsync<TModel>(context, options)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return;
        }

        if (
            !await StateEndpointCodec
                .RequireEtagMatchAsync(context, precondition.IfMatch, snapshot.EtagHex)
                .ConfigureAwait(false)
        )
        {
            return;
        }

        var baseline = await StateEndpointPatch
            .CreateBaselineAsync(context, snapshot.CanonicalJson, options.SerializerOptions)
            .ConfigureAwait(false);
        if (baseline is null)
        {
            return;
        }

        var applied = await StateEndpointPatch
            .ApplyAsync(context, baseline.Node, document, options.SerializerOptions)
            .ConfigureAwait(false);
        if (applied is null)
        {
            return;
        }

        var descriptor = ConfiglueModelDescriptor<TModel>.Current;
        if (applied.IsNoop)
        {
            // No-op (including the empty document): validate If-Match, then succeed
            // without physical writes and without emitting a state-change event.
            await StateEndpointCodec
                .WriteStateResponseAsync(context, descriptor, snapshot.Value, options)
                .ConfigureAwait(false);
            return;
        }

        var desired = await StateEndpointPatch
            .NormalizeAsync(context, descriptor, applied.PatchedJson, options.SerializerOptions)
            .ConfigureAwait(false);
        if (desired is null)
        {
            return;
        }

        // RFC 5789 atomicity gate: determine executability before the first physical write.
        var preview = await StateEndpointWrite
            .PreviewWriteAsync(context, services.EditSessions, desired)
            .ConfigureAwait(false);
        if (preview is null)
        {
            return;
        }

        if (preview.IsEmpty)
        {
            await StateEndpointCodec
                .WriteStateResponseAsync(context, descriptor, snapshot.Value, options)
                .ConfigureAwait(false);
            return;
        }

        var committed = await StateEndpointWrite
            .CommitDesiredAsync(context, services.EditSessions, desired)
            .ConfigureAwait(false);
        if (committed is null)
        {
            return;
        }

        await StateEndpointCodec
            .WriteStateResponseAsync(context, descriptor, committed, options)
            .ConfigureAwait(false);
    }

    private static async Task HandleEventsAsync<TModel>(
        HttpContext context,
        ValidatedStateEndpointOptions options
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        var descriptor = ConfiglueModelDescriptor<TModel>.Current;
        IReadOnlyState<TModel> readState;
        try
        {
            readState = context.RequestServices.GetRequiredService<IReadOnlyState<TModel>>();
        }
        catch (InvalidOperationException exception)
        {
            await StateEndpointProblems
                .WriteServicesProblemAsync(context, exception)
                .ConfigureAwait(false);
            return;
        }

        var snapshot = await StateEndpointCodec
            .LoadCurrentSnapshotAsync<TModel>(context, options)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return;
        }

        var currentEtag = StateEndpointSse.ApplyLastEventId(context, snapshot.EtagHex);

        StateEndpointSse.WriteHeaders(context);
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);

        var cancellation = context.RequestAborted;
        using var channel = StateEndpointSse.ChangeChannel<TModel>.Subscribe(
            readState,
            descriptor,
            options,
            currentEtag
        );
        try
        {
            await channel
                .ConvergeAsync(readState, descriptor, options, cancellation)
                .ConfigureAwait(false);
            await channel.PumpAsync(context, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected; the channel is disposed below.
        }
    }

    internal static byte[] EncodeModel<TModel>(
        ConfiglueModelDescriptor<TModel> descriptor,
        TModel value,
        JsonSerializerOptions? options
    )
        where TModel : IConfiglueFacadeModel<TModel> =>
        StateEndpointCodec.EncodeModel(descriptor, value, options);

    internal static TModel DecodeModel<TModel>(
        ConfiglueModelDescriptor<TModel> descriptor,
        byte[] json,
        JsonSerializerOptions? options
    )
        where TModel : IConfiglueFacadeModel<TModel> =>
        StateEndpointCodec.DecodeModel(descriptor, json, options);

    internal static string ComputeEtagHex(byte[] canonicalJson) =>
        StateEndpointPreconditions.ComputeEtagHex(canonicalJson);
}
