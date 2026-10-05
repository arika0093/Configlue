using System.Text.Json;
using Configlue.CompilerServices;
using Configlue.Provider.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Hosting.AspNetCore;

/// <summary>
/// Current-state load/canonicalization and response formatting stage.
/// </summary>
internal static class StateEndpointCodec
{
    public static byte[] EncodeModel<TModel>(
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

    public static TModel DecodeModel<TModel>(
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

    /// <summary>Canonical snapshot of the current effective state.</summary>
    public sealed record StateSnapshot<TModel>(TModel Value, byte[] CanonicalJson, string EtagHex);

    /// <summary>
    /// Loads the current value and canonicalizes it through the generated fragment
    /// converter. Returns null when a problem was written.
    /// </summary>
    public static async Task<StateSnapshot<TModel>?> LoadCurrentSnapshotAsync<TModel>(
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
            return null;
        }

        TModel current;
        try
        {
            current = await readState.GetValueAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is ConfiglueValidationException or InvalidOperationException)
        {
            await StateEndpointProblems
                .WriteReadProblemAsync(context, exception)
                .ConfigureAwait(false);
            return null;
        }

        byte[] canonicalJson;
        try
        {
            canonicalJson = EncodeModel(descriptor, current, options.SerializerOptions);
        }
        catch (Exception exception)
        {
            await StateEndpointProblems
                .WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State serialization failed.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return null;
        }

        return new StateSnapshot<TModel>(
            current,
            canonicalJson,
            StateEndpointPreconditions.ComputeEtagHex(canonicalJson)
        );
    }

    /// <summary>Writes a 200 JSON state response with the current state ETag.</summary>
    public static async Task WriteStateResponseAsync<TModel>(
        HttpContext context,
        ConfiglueModelDescriptor<TModel> descriptor,
        TModel value,
        ValidatedStateEndpointOptions options
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
            await StateEndpointProblems
                .WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State serialization failed.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return;
        }

        var newEtag = StateEndpointPreconditions.ComputeEtagHex(responseJson);
        context.Response.Headers.ETag = StateEndpointPreconditions.FormatEtag(newEtag);
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

    /// <summary>
    /// Verifies a strong <c>If-Match</c> value against the current ETag.
    /// Returns false and writes 412 on mismatch. <c>"*"</c> always matches.
    /// </summary>
    public static async Task<bool> RequireEtagMatchAsync(
        HttpContext context,
        string? ifMatch,
        string currentEtag
    )
    {
        if (
            ifMatch is { } expected
            && !string.Equals(expected, "*", StringComparison.Ordinal)
            && !string.Equals(expected, currentEtag, StringComparison.Ordinal)
        )
        {
            await StateEndpointProblems
                .WritePreconditionFailedProblemAsync(context)
                .ConfigureAwait(false);
            return false;
        }

        return true;
    }
}
