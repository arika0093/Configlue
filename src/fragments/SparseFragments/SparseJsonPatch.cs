using System.ComponentModel;
using System.Text.Json;

#if CONFIGLUE_FRAGMENT_RUNTIME
namespace Configlue;

#else
namespace SparseFragments;

#endif

/// <summary>Entry facade for RFC 6902 import/export over canonical JSON.</summary>
/// <remarks>
/// Generated <c>FromJsonPatch</c>/<c>ToJsonPatch</c> bridges delegate fragment
/// conversion to their generated JSON converters and use
/// <see cref="JsonPatchDocument"/> plus <see cref="JsonPatchEngine"/> for the
/// baseline-aware document transform, keeping this runtime free of ASP.NET
/// dependencies so Configlue.Hosting.AspNetCore and Configlue.Source.Http can reuse it.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Advanced)]
#if CONFIGLUE_FRAGMENT_RUNTIME
public static class ConfiglueJsonPatch
#else
public static class SparseJsonPatch
#endif
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
