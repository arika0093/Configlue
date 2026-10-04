namespace Configlue.DevTools;

/// <summary>
/// Identifies one live state instance bound to DevTools.
/// </summary>
/// <remarks>
/// Development-only discovery metadata. Values are already redacted where required;
/// this record never carries secret plaintext.
/// </remarks>
public sealed record ConfiglueDevToolsStateInfo(
    string ModelId,
    int ModelVersion,
    string StateName,
    string DisplayName,
    string ModelType
);
