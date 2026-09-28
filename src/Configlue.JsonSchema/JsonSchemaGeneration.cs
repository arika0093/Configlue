namespace Configlue.JsonSchema;

internal static class JsonSchemaGeneration
{
    public static string GetSchemaFileName(string modelId, int version) =>
        StateSchemaReference.GetFileName(modelId, version);

    public static bool IsValidModelId(string modelId) =>
        !string.IsNullOrWhiteSpace(modelId)
        && modelId is not "." and not ".."
        && modelId.IndexOf('\0') < 0
        && modelId.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0;
}
