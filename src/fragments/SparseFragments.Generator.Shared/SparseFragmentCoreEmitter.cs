using System.Collections.Generic;
using System.Collections.Immutable;

namespace SparseFragments.Generator.Shared;

/// <summary>Product-neutral fragment algebra emission, configured with runtime names.</summary>
/// <remarks>
/// This type is the shared entry point used by the SparseFragments and Configlue
/// generators. Each generation concept is owned by a focused stage emitter; this
/// class only wires them together so both products keep consuming one surface.
/// </remarks>
internal sealed class SparseFragmentCoreEmitter
{
    private readonly SparseFragmentDeclarationEmitter _declaration;
    private readonly SparseFragmentConversionEmitter _conversion;
    private readonly SparseFragmentMergeEmitter _merge;
    private readonly SparseFragmentCloneEmitter _clone;

    public SparseFragmentCoreEmitter(
        string optional,
        string mergeStrategyFieldPrefix,
        string cloneContext,
        string referenceComparer,
        SparseFragmentExpressions expressions
    )
    {
        _declaration = new(optional, mergeStrategyFieldPrefix);
        _conversion = new(optional, cloneContext, referenceComparer, expressions);
        _merge = new(optional, mergeStrategyFieldPrefix, expressions);
        _clone = new(optional, cloneContext, referenceComparer, expressions);
    }

    public static bool RequiresPortableSetView(
        bool bclHashSetImplementsReadOnlySet,
        IEnumerable<string?> namedTypeDefinitions
    ) =>
        SparseFragmentCollectionCloneEmitter.RequiresPortableSetView(
            bclHashSetImplementsReadOnlySet,
            namedTypeDefinitions
        );

    public string MergeStrategyField(SparseMemberModel member) =>
        _declaration.MergeStrategyField(member);

    public void AppendBuilder(
        SharedIndentedBuilder code,
        ImmutableArray<SparseMemberModel> members
    ) => _declaration.AppendBuilder(code, members);

    public static void AppendDeclaration(
        SharedIndentedBuilder code,
        string fragmentInterface,
        string deepCloneable,
        System.Action<SharedIndentedBuilder>? appendAttributes = null,
        string? advancedInterface = null
    ) =>
        SparseFragmentDeclarationEmitter.AppendDeclaration(
            code,
            fragmentInterface,
            deepCloneable,
            appendAttributes,
            advancedInterface
        );

    public void AppendMembers(
        SharedIndentedBuilder code,
        ImmutableArray<SparseMemberModel> members,
        string mergeStrategy,
        System.Action<SharedIndentedBuilder>? appendMemberAttributes = null
    ) => _declaration.AppendMembers(code, members, mergeStrategy, appendMemberAttributes);

    public static void AppendCollectionCloneHelpers(
        SharedIndentedBuilder code,
        bool includePortableSetView,
        bool hashSetSupportsCapacity
    ) =>
        SparseFragmentCollectionCloneEmitter.AppendCollectionCloneHelpers(
            code,
            includePortableSetView,
            hashSetSupportsCapacity
        );

    public void AppendDeepClone(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members,
        bool usesPocoCloning,
        ModelConstructorBinding? constructor = null,
        bool modelIsReferenceType = true
    ) =>
        _clone.AppendDeepClone(
            code,
            modelType,
            members,
            usesPocoCloning,
            constructor,
            modelIsReferenceType
        );

    public void AppendPocoCloneHelper(
        SharedIndentedBuilder code,
        string typeName,
        string cloneHelperName,
        ImmutableArray<SparseMemberModel> members,
        ModelConstructorBinding? constructor = null
    ) => _clone.AppendPocoCloneHelper(code, typeName, cloneHelperName, members, constructor);

    public void AppendFromModel(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members,
        bool modelIsReferenceType,
        bool usesPocoCloning
    ) =>
        _conversion.AppendFromModel(
            code,
            modelType,
            members,
            modelIsReferenceType,
            usesPocoCloning
        );

    public static void AppendRootProjectionConstructor(
        SharedIndentedBuilder code,
        string modelName,
        ImmutableArray<SparseMemberModel> members,
        ModelConstructorBinding? constructor = null
    ) =>
        SparseFragmentConversionEmitter.AppendRootProjectionConstructor(
            code,
            modelName,
            members,
            constructor
        );

    public static void AppendToModel(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members,
        bool hasRootProjectionConstructor = false,
        ModelConstructorBinding? constructor = null
    ) =>
        SparseFragmentConversionEmitter.AppendToModel(
            code,
            modelType,
            members,
            hasRootProjectionConstructor,
            constructor
        );

    public void AppendMerge(
        SharedIndentedBuilder code,
        ImmutableArray<SparseMemberModel> members
    ) => _merge.AppendMerge(code, members);

    public void AppendApplyChanges(
        SharedIndentedBuilder code,
        ImmutableArray<SparseMemberModel> members
    ) => _merge.AppendApplyChanges(code, members);

    public void AppendDiff(
        SharedIndentedBuilder code,
        string modelType,
        ImmutableArray<SparseMemberModel> members,
        bool modelIsReferenceType
    ) => _merge.AppendDiff(code, modelType, members, modelIsReferenceType);

    public void AppendFragmentClone(
        SharedIndentedBuilder code,
        ImmutableArray<SparseMemberModel> members,
        bool usesPocoCloning
    ) => _clone.AppendFragmentClone(code, members, usesPocoCloning);
}
