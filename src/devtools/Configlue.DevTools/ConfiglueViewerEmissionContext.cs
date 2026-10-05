using System.Text.Json;
using Configlue.CompilerServices;

namespace Configlue.DevTools;

/// <summary>
/// Mutable accumulators for a single viewer document projection.
/// </summary>
/// <remarks>
/// <para>
/// Represents exactly one projection operation: the position-tracking writer,
/// the details snapshot and naming policy needed to emit it, and the
/// ranges/decorations/hovers/markers accumulated along the way.
/// </para>
/// <para>
/// Intentionally narrow: no services, caches, or unrelated configuration.
/// Secret redaction stays explicit at the emission boundary via
/// <see cref="AppendSecretPlaceholder"/>; callers never write secret
/// plaintext through this context.
/// </para>
/// </remarks>
internal sealed class ConfiglueViewerEmissionContext
{
    public ConfiglueViewerEmissionContext(
        ConfiglueDetailsSnapshot? snapshot,
        JsonNamingPolicy? namingPolicy,
        string indent
    )
    {
        Snapshot = snapshot;
        NamingPolicy = namingPolicy;
        Writer = new ConfiglueViewerPositionWriter(indent);
    }

    public ConfiglueViewerPositionWriter Writer { get; }

    public ConfiglueDetailsSnapshot? Snapshot { get; }

    public JsonNamingPolicy? NamingPolicy { get; }

    public List<ConfiglueViewerMemberRange> MemberRanges { get; } = new();

    public List<ConfiglueViewerDecoration> Decorations { get; } = new();

    public List<ConfiglueViewerHover> Hovers { get; } = new();

    public List<ConfiglueViewerMarker> Markers { get; } = new();

    /// <summary>
    /// Explicit secret-redaction boundary: emits the stable placeholder
    /// instead of secret plaintext.
    /// </summary>
    public void AppendSecretPlaceholder()
    {
        Writer.AppendQuoted(ConfiglueSecrets.RedactedText);
    }

    public void AddMemberRange(
        string memberPath,
        string wireName,
        ConfiglueViewerRange nameRange,
        ConfiglueViewerRange valueRange,
        bool isSecret
    )
    {
        MemberRanges.Add(
            new ConfiglueViewerMemberRange(memberPath, wireName, nameRange, valueRange, isSecret)
        );
    }

    public ConfiglueViewerDocument ToDocument(
        string modelId,
        int modelVersion,
        long documentVersion,
        string schemaUri
    )
    {
        return new ConfiglueViewerDocument(
            modelId,
            modelVersion,
            documentVersion,
            Writer.ToString(),
            schemaUri,
            MemberRanges.AsReadOnly(),
            Decorations.AsReadOnly(),
            Hovers.AsReadOnly(),
            Markers.AsReadOnly()
        );
    }
}
