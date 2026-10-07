global using CloneCollectionKind = SparseFragments.Generator.Shared.SparseCloneCollectionKind;
global using CollectionKind = SparseFragments.Generator.Shared.SparseCollectionKind;
using SparseFragments.Generator.Shared;

namespace Configlue.Generator;

public sealed partial class ConfiglueGenerator
{
    private static readonly SparseFragmentCoreEmitter FragmentCore = new(
        "global::Configlue.Optional",
        "__configlue_merge_strategy_",
        "__configlue_clone_context",
        "global::Configlue.CompilerServices.ConfiglueReferenceEqualityComparer",
        new SparseFragmentExpressions(
            "__configlue_clone_context",
            "global::Configlue.ConfiglueValueComparer",
            "global::Configlue.ConfiglueCollectionMerger"
        )
    );

    private static SparseTypeModel ToSparseType(TypeModel type) =>
        new(
            type.Name,
            type.NonNullableName,
            type.RuntimeName,
            type.IsReferenceType,
            type.IsConfiglueType,
            type.PocoCloneHelperName
        );

    private static SparseMemberModel ToSparseMember(MemberModel member) =>
        ToSparseMember(member, false);

    private static SparseMemberModel ToSparseMember(MemberModel member, bool portableSetView) =>
        new(
            member.Id,
            new SparsePropertyModel(
                member.Property.Name,
                ToSparseType(member.Property.Type),
                member.Property.IsInitOnly,
                member.Property.IsRequired,
                member.Property.IsReadOnly,
                member.Property.JsonPropertyName,
                member.Property.HasExplicitJsonPropertyName,
                member.Property.JsonIgnoreCondition
            ),
            member.ChildModel is { } child ? ToSparseType(child) : null,
            member.MergeMode,
            new SparseCollectionInfo(
                member.Collection.Kind,
                member.Collection.CloneKind,
                ToSparseType(member.Collection.ElementType),
                member.Collection.ValueType is { } value ? ToSparseType(value) : null,
                member.Collection.NamedTypeDefinition
            ),
            member.MergeStrategyType is { } strategy ? ToSparseType(strategy) : null,
            member.ChildFragmentType,
            member.ChildIsStructural,
            member.ChildIsReferenceType,
            portableSetView
        );

    private static readonly SparseGeneratorConfig SparseConfiguration = new(
        ModelAttributeName,
        MergeAttributeName,
        "Configlue.ConfiglueMergeStrategy<T>",
        "Configlue.ConfiglueCloneReferenceSafeAttribute",
        // Non-partial nested POCOs keep Configlue's structural semantics: they
        // receive generated structural hosts for deep behavior instead of
        // collapsing to atomic replace values.
        SparseStructuralPolicy.StructuralHosts
    );

    // JSON Patch import dialect for the shared semantic Between emitter: whole-state
    // transitions use the whole operation, nested members recurse into the nested
    // __ConfiglueJsonBetween helper, and scalar members use ordinal default equality
    // (over-setting is semantically harmless, under-setting never happens).
    private static readonly SparseBetweenDialect ConfiglueBetweenDialect = new(
        "global::Configlue.",
        "global::Configlue.Optional<Fragment?>",
        "global::Configlue.FragmentOperation<Fragment?>",
        "__configlue_whole_operation",
        "__ConfiglueJson",
        static member =>
            member.ChildModel is null
                ? SparseNaming.EscapeIdentifier(member.Property.Name)
                : "__configlue_member_" + member.Property.Name,
        static member => member.ChildModel is null,
        static member =>
            member.ChildModel is null ? member.Property.Type.Name : member.ChildFragmentType + "?",
        static (member, beforeValue, afterValue) =>
            "global::System.Collections.Generic.EqualityComparer<"
            + (
                member.ChildModel is null
                    ? member.Property.Type.Name
                    : member.ChildFragmentType + "?"
            )
            + ">.Default.Equals("
            + beforeValue
            + "!, "
            + afterValue
            + "!)",
        static (member, beforeAccess, afterAccess) =>
            NestedPatchBetweenName(member) + "(" + beforeAccess + ", " + afterAccess + ")"
    );

    private static string NestedPatchBetweenName(SparseMemberModel member)
    {
        var fragmentType =
            member.ChildFragmentType
            ?? throw new InvalidOperationException("Nested member is missing its fragment type.");
        return fragmentType.Substring(0, fragmentType.Length - "Fragment".Length)
            + "Patch.__ConfiglueJsonBetween";
    }
}
