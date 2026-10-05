namespace SparseFragments.Generator.Shared;

internal static class SparseWellKnownNames
{
    public const string RuntimeNamespace = "global::SparseFragments";
    public const string OptionalType = RuntimeNamespace + ".Optional";
    public const string FragmentMemberType = RuntimeNamespace + ".SparseFragmentMember";
    public const string FragmentInterfaceType = RuntimeNamespace + ".ISparseFragment";
    public const string DeepCloneableType = RuntimeNamespace + ".ISparseDeepCloneable";
    public const string SchemaType = RuntimeNamespace + ".SparseFragmentSchema";
    public const string MemberSchemaType = RuntimeNamespace + ".SparseFragmentMemberSchema";
    public const string MergeStrategyType = RuntimeNamespace + ".FragmentMergeStrategy";
    public const string ValueComparerType = RuntimeNamespace + ".SparseValueComparer";
    public const string ReferenceComparerType =
        RuntimeNamespace + ".SparseReferenceEqualityComparer";
    public const string CollectionMergerType = RuntimeNamespace + ".SparseCollectionMerger";

    public const string InterfaceSetTypeDefinition = "System.Collections.Generic.ISet<T>";
    public const string ReadOnlySetTypeDefinition = "System.Collections.Generic.IReadOnlySet<T>";

    public const string FragmentTypeName = "Fragment";
    public const string HintNameSuffix = ".SparseFragments.g.cs";
    public const string StructuralHostPrefix = "__SparseStructural_";
    public const string CloneHelperPrefix = "__Clone_";
    public const string MergeStrategyFieldPrefix = "__sparse_merge_strategy_";
}

internal static class SparseDiagnosticIds
{
    public const string MustBePartial = "SPF001";
    public const string UnsupportedModel = "SPF002";
    public const string MissingConstructor = "SPF003";
    public const string InvalidMergeStrategy = "SPF004";
    public const string UnsupportedMerge = "SPF005";
    public const string UnsupportedRequired = "SPF006";
    public const string UnsupportedStructural = "SPF007";
    public const string UnsupportedClone = "SPF008";
    public const string GeneratedNameCollision = "SPF009";
    public const string IncompatiblePromotedModel = "SPF010";
}
