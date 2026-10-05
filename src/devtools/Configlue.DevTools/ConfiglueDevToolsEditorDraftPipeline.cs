using System.Text.Json;
using System.Text.Json.Nodes;
using Configlue.CompilerServices;

namespace Configlue.DevTools;

/// <summary>
/// Draft JSON pipeline behind the DevTools editor session: parse, normalize,
/// diff, and merge a Monaco draft into a model value.
/// </summary>
/// <remarks>
/// <para>
/// Internal to the DevTools package. The Monaco document is an effective-state
/// projection, never a storage document: this pipeline only derives the desired
/// model value. Secret placeholders are resolved to current values (or dropped
/// when nothing is known, so a fake placeholder is never persisted); editability
/// and secret guards run in their own collaborators before and after the merge.
/// </para>
/// </remarks>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "SonarAnalyzer.CSharp",
    "S2743",
    Justification = "The options instance is stateless configuration shared across all closed model types by design."
)]
internal sealed class ConfiglueDevToolsEditorDraftPipeline<TModel>
    where TModel : IConfiglueFacadeModel<TModel>
{
    private static readonly JsonSerializerOptions StrictModelOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    private readonly ConfiglueModelSchema _schema;
    private readonly JsonNamingPolicy? _namingPolicy;

    public ConfiglueDevToolsEditorDraftPipeline(
        ConfiglueModelSchema schema,
        JsonNamingPolicy? namingPolicy
    )
    {
        _schema = schema;
        _namingPolicy = namingPolicy;
    }

    /// <summary>
    /// Parses the draft text; the root must be a JSON object.
    /// </summary>
    public bool TryParseRoot(
        string draftJson,
        out JsonObject? draft,
        out ConfiglueEditorFailureCategory category,
        out string error
    )
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(draftJson);
        }
        catch (JsonException exception)
        {
            draft = null;
            category = ConfiglueEditorFailureCategory.Parse;
            error =
                $"The draft is not well-formed JSON: {ConfiglueDevToolsEditorFailures.TrimMessage(exception.Message)}";
            return false;
        }

        if (node is not JsonObject obj)
        {
            draft = null;
            category = ConfiglueEditorFailureCategory.Schema;
            error = "The draft root must be a JSON object matching the effective state.";
            return false;
        }

        draft = obj;
        category = ConfiglueEditorFailureCategory.None;
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Normalizes the draft and baseline keys to generated member names.
    /// </summary>
    public bool TryNormalize(
        JsonObject draft,
        string baselineJson,
        out JsonObject? normalizedDraft,
        out JsonObject? normalizedBaseline
    )
    {
        var baselineNode = JsonNode.Parse(baselineJson) as JsonObject;
        normalizedDraft =
            ConfiglueEditorSemanticJson.NormalizeKeys(draft, _schema, _namingPolicy) as JsonObject;
        normalizedBaseline =
            ConfiglueEditorSemanticJson.NormalizeKeys(baselineNode, _schema, _namingPolicy)
            as JsonObject;
        return normalizedDraft is not null && normalizedBaseline is not null;
    }

    /// <summary>
    /// Whether the normalized draft carries the same effective value as the baseline.
    /// </summary>
    public static bool IsNoOp(JsonObject normalizedBaseline, JsonObject normalizedDraft) =>
        ConfiglueEditorSemanticJson.SemanticEquals(normalizedBaseline, normalizedDraft);

    /// <summary>
    /// Collects changed leaf paths (generated member names) between baseline and draft.
    /// </summary>
    public List<string> CollectChanges(JsonObject normalizedBaseline, JsonObject normalizedDraft)
    {
        var rawPaths = new List<string>();
        ConfiglueEditorSemanticJson.CollectChangedPaths(
            normalizedBaseline,
            normalizedDraft,
            _schema,
            prefix: string.Empty,
            rawPaths
        );
        return rawPaths;
    }

    /// <summary>
    /// Merges the draft over the current value and binds it strictly to the model.
    /// </summary>
    public bool TryMerge(TModel before, JsonObject draft, out TModel desired, out string error)
    {
        string mergedJson;
        try
        {
            var currentNode = JsonSerializer.SerializeToNode(before) as JsonObject;
            var merged = ConfiglueEditorSemanticJson.MergeDraft(
                currentNode,
                draft,
                _schema,
                _namingPolicy
            );
            mergedJson = merged.ToJsonString();
        }
        catch (Exception exception)
            when (exception is InvalidOperationException || exception is JsonException)
        {
            desired = before;
            error =
                $"The draft could not be merged over the current value: {ConfiglueDevToolsEditorFailures.TrimMessage(exception.Message)}";
            return false;
        }

        try
        {
            desired =
                JsonSerializer.Deserialize<TModel>(mergedJson, StrictModelOptions)
                ?? throw new InvalidOperationException(
                    "The DevTools draft did not contain a model value."
                );
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
            when (exception is JsonException || exception is InvalidOperationException)
        {
            desired = before;
            error =
                $"The draft does not match the model shape: {ConfiglueDevToolsEditorFailures.TrimMessage(exception.Message)}";
            return false;
        }
    }
}
