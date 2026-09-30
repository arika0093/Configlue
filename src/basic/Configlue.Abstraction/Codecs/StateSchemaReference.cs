namespace Configlue.Codecs;

/// <summary>Builds references to versioned JSON Schema files.</summary>
public static class StateSchemaReference
{
    /// <summary>Gets the generated schema file name for a model identity and version.</summary>
    public static string GetFileName(string modelId, int version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (version < StateSchemaMetadata.InitialVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        if (
            modelId is "." or ".."
            || modelId.IndexOf('\0') >= 0
            || modelId.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0
        )
        {
            throw new ArgumentException(
                $"Model ID '{modelId}' cannot be used as a schema file name.",
                nameof(modelId)
            );
        }

        return $"{modelId}.v{version}.json";
    }

    /// <summary>Resolves a versioned schema file against an absolute or relative directory URI.</summary>
    public static string CreateUri(string baseUri, StateSchemaMetadata schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUri);
        if (schema.ModelId is null)
        {
            throw new ArgumentException(
                "A model ID is required to create a schema reference.",
                nameof(schema)
            );
        }

        if (
            baseUri.IndexOfAny(['?', '#', '\r', '\n']) >= 0
            || !Uri.TryCreate(baseUri, UriKind.RelativeOrAbsolute, out var parsedBaseUri)
        )
        {
            throw new ArgumentException(
                "The schema reference base URI must be a directory URI without a query or fragment.",
                nameof(baseUri)
            );
        }

        var fileName = GetFileName(schema.ModelId, schema.Version);
        var escapedFileName = Uri.EscapeDataString(fileName);
        if (parsedBaseUri.IsAbsoluteUri)
        {
            var builder = new UriBuilder(parsedBaseUri);
            if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
            {
                builder.Path += "/";
            }

            return new Uri(builder.Uri, escapedFileName).AbsoluteUri;
        }

        return baseUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri + escapedFileName
            : baseUri + "/" + escapedFileName;
    }
}
