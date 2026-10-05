using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Configlue.CompilerServices;
using Configlue.Provider.Json;
using Microsoft.AspNetCore.Http;

namespace Configlue.Hosting.AspNetCore;

/// <summary>Patch application and typed normalization stage (RFC 6902 over the GET shape).</summary>
internal static class StateEndpointPatch
{
    public sealed record PatchBaseline(JsonNode? Node, byte[] CanonicalJson, string EtagHex);

    /// <summary>Parses the request body as an RFC 6902 document. Null when a 400 was written.</summary>
    public static async Task<JsonPatchDocument?> ParseDocumentAsync(
        HttpContext context,
        byte[] body
    )
    {
        try
        {
            return ConfiglueJsonPatch.Parse(body);
        }
        catch (JsonPatchException exception)
        {
            await StateEndpointProblems
                .WriteProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Malformed JSON Patch document.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Builds the patch baseline from canonical GET JSON. Null when a 500 was written.
    /// PATCH paths address the exact JSON shape returned by GET.
    /// </summary>
    public static async Task<PatchBaseline?> CreateBaselineAsync(
        HttpContext context,
        byte[] canonicalJson,
        JsonSerializerOptions? serializerOptions
    )
    {
        JsonNode? baselineNode;
        try
        {
            baselineNode = JsonNode.Parse(canonicalJson);
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

        return new PatchBaseline(
            baselineNode,
            canonicalJson,
            StateEndpointPreconditions.ComputeEtagHex(canonicalJson)
        );
    }

    public sealed record PatchOutcome(bool IsNoop, byte[] PatchedJson);

    /// <summary>
    /// Applies the document to the baseline node. Null when a 400/409 was written.
    /// A null outcome-node / absent result normalizes to <c>{}</c> (model defaults),
    /// exactly like a removed fixed-schema property reappearing on the next GET.
    /// </summary>
    public static async Task<PatchOutcome?> ApplyAsync(
        HttpContext context,
        JsonNode? baselineNode,
        JsonPatchDocument document,
        JsonSerializerOptions? serializerOptions
    )
    {
        var effectiveOptions = ConfiglueFragmentJson.CreateOptions(serializerOptions);
        var comparison = effectiveOptions.PropertyNameCaseInsensitive
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        JsonPatchEngine.ApplyResult applied;
        try
        {
            applied = ConfiglueJsonPatch.Apply(baselineNode, false, document, comparison);
        }
        catch (JsonPatchException exception)
        {
            await StateEndpointProblems
                .WritePatchApplyProblemAsync(context, exception)
                .ConfigureAwait(false);
            return null;
        }

        if (
            applied is { IsAbsent: false, Node: not null }
            && JsonNode.DeepEquals(applied.Node, baselineNode)
        )
        {
            return new PatchOutcome(IsNoop: true, PatchedJson: []);
        }

        if (applied is { IsAbsent: true } or { Node: null })
        {
            return new PatchOutcome(IsNoop: false, PatchedJson: "{}"u8.ToArray());
        }

        return new PatchOutcome(
            IsNoop: false,
            PatchedJson: Encoding.UTF8.GetBytes(applied.Node.ToJsonString())
        );
    }

    /// <summary>
    /// Normalizes patched JSON through the fragment type (strict, unmapped disallowed)
    /// and the generated model. Null when a 409/500 was written.
    /// </summary>
    public static async Task<TModel?> NormalizeAsync<TModel>(
        HttpContext context,
        ConfiglueModelDescriptor<TModel> descriptor,
        byte[] patchedJson,
        JsonSerializerOptions? serializerOptions
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        object patchedFragment;
        try
        {
            var strict = ConfiglueFragmentJson.CreateOptions(serializerOptions);
            strict.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            patchedFragment = ConfiglueFragmentJson.Deserialize(
                descriptor.FragmentType,
                patchedJson,
                strict
            );
        }
        catch (JsonException exception)
        {
            await StateEndpointProblems
                .WriteProblemAsync(
                    context,
                    StatusCodes.Status409Conflict,
                    "The JSON Patch cannot be applied to the current state.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return default;
        }
        catch (InvalidOperationException exception)
        {
            await StateEndpointProblems
                .WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State services are not registered.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return default;
        }

        try
        {
            return (TModel)descriptor.FromFragmentBoxed(patchedFragment);
        }
        catch (Exception exception)
        {
            await StateEndpointProblems
                .WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State normalization failed.",
                    exception.Message
                )
                .ConfigureAwait(false);
            return default;
        }
    }
}
