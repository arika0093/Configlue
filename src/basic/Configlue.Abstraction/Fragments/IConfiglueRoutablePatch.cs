namespace Configlue;

/// <summary>A generated patch that can split operations across property-path source routes.</summary>
public interface IConfiglueRoutablePatch : IConfiglueMemberPatch
{
    /// <summary>Creates one source-local patch per target in a write plan.</summary>
    IReadOnlyDictionary<SourceId, IConfigluePatch> Route(
        StateWritePlan writePlan,
        SourceId? fallbackSourceId
    );
}
