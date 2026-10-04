namespace Configlue.DevTools;

/// <summary>
/// One changed member path observed while synchronizing a Monaco draft.
/// </summary>
/// <remarks>
/// Development-only editor metadata. <see cref="MemberPath"/> uses generated
/// member names (<c>Database.Port</c>, collection elements as <c>Tags[0]</c>);
/// <see cref="Editability"/> reports the member's current
/// <see cref="ConfiglueEditability"/>.
/// </remarks>
public sealed record ConfiglueEditorChangedPath(string MemberPath, string Editability);

/// <summary>Outcome of synchronizing one Monaco draft into the owned edit session.</summary>
/// <remarks>
/// Development-only editor metadata. Synchronization parses the projection text,
/// rejects protected ranges and secret plaintext, and applies the desired model to
/// the server-side <see cref="EditSession{T}"/> draft. It never writes a source.
/// </remarks>
public sealed record ConfiglueEditorSyncResult(
    bool Success,
    ConfiglueEditorFailureCategory Category,
    IReadOnlyList<string> Errors,
    IReadOnlyList<ConfiglueEditorChangedPath> ChangedPaths,
    int ModifiedCount,
    bool HasChanges,
    bool HasUpstreamChanges
)
{
    /// <summary>Creates a successful synchronization outcome.</summary>
    public static ConfiglueEditorSyncResult Ok(
        IReadOnlyList<ConfiglueEditorChangedPath> changedPaths,
        bool hasChanges,
        bool hasUpstreamChanges
    ) =>
        new(
            true,
            ConfiglueEditorFailureCategory.None,
            [],
            changedPaths,
            changedPaths.Count,
            hasChanges,
            hasUpstreamChanges
        );

    /// <summary>Creates a rejected synchronization outcome.</summary>
    public static ConfiglueEditorSyncResult Fail(
        ConfiglueEditorFailureCategory category,
        IReadOnlyList<string> errors,
        bool hasUpstreamChanges
    ) => new(false, category, errors, [], 0, false, hasUpstreamChanges);
}

/// <summary>Outcome of a dry-run write preview (routing plus Configlue validation).</summary>
/// <remarks>
/// Development-only editor metadata. Computed without mutating any source or
/// resource; a concurrent change between the preview and the commit can still
/// conflict or reroute the write.
/// </remarks>
public sealed record ConfiglueEditorPreviewResult(
    bool Success,
    ConfiglueEditorFailureCategory Category,
    IReadOnlyList<string> Errors,
    int PhysicalWriteCount,
    bool IsAtomic,
    bool IsEmpty,
    bool PreviewAvailable
)
{
    /// <summary>Creates a successful preview outcome.</summary>
    public static ConfiglueEditorPreviewResult Ok(
        int physicalWriteCount,
        bool isAtomic,
        bool isEmpty
    ) =>
        new(
            true,
            ConfiglueEditorFailureCategory.None,
            [],
            physicalWriteCount,
            isAtomic,
            isEmpty,
            true
        );

    /// <summary>Creates a successful outcome when the runtime exposes no preview.</summary>
    public static ConfiglueEditorPreviewResult Unavailable() =>
        new(true, ConfiglueEditorFailureCategory.None, [], -1, false, false, false);

    /// <summary>Creates a rejected preview outcome.</summary>
    public static ConfiglueEditorPreviewResult Fail(
        ConfiglueEditorFailureCategory category,
        IReadOnlyList<string> errors
    ) => new(false, category, errors, -1, false, false, true);
}

/// <summary>One logical source touched by a committed editor save.</summary>
/// <remarks>Development-only editor metadata over normal Configlue write receipts.</remarks>
public sealed record ConfiglueEditorSourceWrite(
    string SourceId,
    string? ResourceId,
    string? Revision
);

/// <summary>Summary of one committed editor save across routed sources.</summary>
/// <remarks>
/// Development-only editor metadata. One logical save may route members to
/// different sources; every touched source is listed, never flattened.
/// </remarks>
public sealed record ConfiglueEditorWriteReceipt(
    IReadOnlyList<ConfiglueEditorSourceWrite> Sources,
    int PhysicalWriteCount,
    bool IsAtomic,
    string StateName,
    string? Revision
);

/// <summary>Outcome of committing the owned edit session through normal write routing.</summary>
/// <remarks>Development-only editor metadata.</remarks>
public sealed record ConfiglueEditorSaveResult(
    bool Committed,
    ConfiglueEditorFailureCategory Category,
    IReadOnlyList<string> Errors,
    ConfiglueEditorWriteReceipt? Receipt,
    IReadOnlyList<string> CommittedPaths,
    bool HasUpstreamChanges
)
{
    /// <summary>Creates a successful save outcome.</summary>
    public static ConfiglueEditorSaveResult Ok(
        ConfiglueEditorWriteReceipt receipt,
        IReadOnlyList<string> committedPaths,
        bool hasUpstreamChanges
    ) =>
        new(
            true,
            ConfiglueEditorFailureCategory.None,
            [],
            receipt,
            committedPaths,
            hasUpstreamChanges
        );

    /// <summary>Creates a rejected save outcome.</summary>
    public static ConfiglueEditorSaveResult Fail(
        ConfiglueEditorFailureCategory category,
        IReadOnlyList<string> errors,
        bool hasUpstreamChanges
    ) => new(false, category, errors, null, [], hasUpstreamChanges);
}
