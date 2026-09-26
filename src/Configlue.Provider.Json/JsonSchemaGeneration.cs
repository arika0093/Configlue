namespace Configlue.Provider.Json;

internal static class JsonSchemaGeneration
{
    public static string GetSchemaFileName(string modelId, int version)
    {
        ValidateModelId(modelId);
        if (version < StateSchemaMetadata.InitialVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        return $"{modelId}.v{version}.json";
    }

    public static bool IsValidModelId(string modelId) =>
        !string.IsNullOrWhiteSpace(modelId)
        && modelId is not "." and not ".."
        && modelId.IndexOf('\0') < 0
        && modelId.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0;

    private static void ValidateModelId(string modelId)
    {
        if (!IsValidModelId(modelId))
        {
            throw new ArgumentException(
                $"Model ID '{modelId}' cannot be used as a schema file name.",
                nameof(modelId)
            );
        }
    }
}
