using System.ComponentModel;
using System.Text.Json;

namespace Configlue;

/// <summary>Entry facade for RFC 6902 import/export over canonical JSON.</summary>
/// <remarks>
/// Thin Configlue adapter over the product-neutral <see cref="JsonPatchDocument"/>
/// plus <see cref="JsonPatchEngine"/> runtime (mirrored from
/// <c>src/fragments:src/SparseFragments/JsonPatch/</c>, namespace
/// <c>SparseFragments</c>; no runtime dependency between the packages).
/// Generated <c>FromJsonPatch</c>/<c>ToJsonPatch</c> bridges delegate fragment
/// conversion to their generated JSON converters and use
/// <see cref="JsonPatchDocument"/> plus <see cref="JsonPatchEngine"/> for the
/// baseline-aware document transform, keeping this runtime free of ASP.NET
/// dependencies so Configlue.Hosting.AspNetCore and Configlue.Source.Http can reuse it.
/// <para>
/// Single-implementation cutover is blocked upstream, not here: the SparseFragments
/// sources declare <c>namespace SparseFragments</c> product types, so compiling
/// them into Configlue would either leak SparseFragments public types into the
/// Configlue package or require a neutral-engine refactor that must land upstream
/// (this repo holds <c>src/fragments</c> as a read-only submodule). Concretely,
/// upstream still needs: a product-neutral engine compilation unit — neutral
/// namespace (for example <c>SparseFragments.JsonPatch.Neutral</c> or a new
/// shared assembly), neutral exception/error-kind surface or a converter mapping
/// to <c>SparseFragments.JsonPatchException</c>/<c>JsonPatchErrorKind</c>, and a
/// neutral <c>RfcJsonEquality</c> plus <c>JsonPointer</c> usable without the
/// product namespaces — after which both runtimes compile that unit and keep
/// only thin product adapters (<c>ConfiglueJsonPatch</c> here,
/// <c>SparseJsonPatch</c> there).
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public static class ConfiglueJsonPatch
{
    /// <summary>Parses UTF-8 JSON Patch bytes.</summary>
    public static JsonPatchDocument Parse(ReadOnlyMemory<byte> utf8) =>
        JsonPatchDocument.Parse(utf8);

    /// <summary>Parses a JSON Patch string.</summary>
    public static JsonPatchDocument Parse(string json) => JsonPatchDocument.Parse(json);

    /// <summary>Serializes a patch document to UTF-8 bytes.</summary>
    public static byte[] Serialize(
        JsonPatchDocument document,
        JsonSerializerOptions? options = null
    ) => JsonPatchEngine.Serialize(document, options);

    /// <summary>Applies a patch document to a baseline JSON state atomically.</summary>
    public static JsonPatchEngine.ApplyResult Apply(
        System.Text.Json.Nodes.JsonNode? baseline,
        bool baselineIsAbsent,
        JsonPatchDocument document,
        StringComparison propertyNameComparison = StringComparison.Ordinal
    ) => JsonPatchEngine.Apply(baseline, baselineIsAbsent, document, propertyNameComparison);

    /// <summary>Difs two JSON states into a semantically equivalent patch document.</summary>
    public static JsonPatchDocument Diff(
        System.Text.Json.Nodes.JsonNode? before,
        bool beforeIsAbsent,
        System.Text.Json.Nodes.JsonNode? after,
        bool afterIsAbsent
    ) => JsonPatchEngine.Diff(before, beforeIsAbsent, after, afterIsAbsent);
}
