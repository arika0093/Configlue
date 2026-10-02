namespace Configlue.CompilerServices;

/// <summary>A generated patch that can split operations across property-path source routes.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueRoutablePatch : IConfiglueDynamicMemberPatch
{
    /// <summary>Creates one source-local patch per target in a write plan.</summary>
    IReadOnlyDictionary<SourceId, IConfigluePatch> Route(
        StateWritePlan writePlan,
        SourceId? fallbackSourceId
    );
}
