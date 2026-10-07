using System.Collections.Immutable;
using System.Linq;
using SparseFragments.Generator.Shared;

namespace Configlue.Generator;

public sealed partial class ConfiglueGenerator
{
    // Bindable proxy generation is owned by the shared SparseFragments emitter
    // (SparseObservableEmitter, upstream SparseFragments #36). This file is only
    // orchestration: convert Configlue member models via the ToSparseMember bridge
    // and delegate, so there is a single emitter implementation.
    //
    // Configlue-specific concerns (schema/version metadata, secrets, provenance,
    // storage registration) stay outside the shared emitter; only structural
    // member info flows through ToSparseMember. Call-site filtering (which
    // models get an Observable) is unchanged from before.
    private static void AppendObservableModel(
        IndentedStringBuilder code,
        string valueType,
        bool valueIsReferenceType,
        ImmutableArray<MemberModel> members
    )
    {
        // The shared emitter always null-guards the wrapped model; the legacy
        // reference-type-only guard is intentionally not preserved.
        _ = valueIsReferenceType;
        var sparseMembers = members.Select(ToSparseMember).ToImmutableArray();
        SparseObservableEmitter.AppendObservable(code, valueType, sparseMembers);
    }
}
