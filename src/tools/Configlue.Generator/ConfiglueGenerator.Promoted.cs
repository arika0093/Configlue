using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Configlue.Generator;

public sealed partial class ConfiglueGenerator
{
    private sealed class PromotedModel : IEquatable<PromotedModel>
    {
        public PromotedModel(
            ModelInfo model,
            ImmutableArray<MemberModel> members,
            ImmutableArray<PocoCloneModel> pocoCloneModels,
            ImmutableArray<StructuralModel> structuralModels
        )
        {
            Model = model;
            Members = members;
            PocoCloneModels = pocoCloneModels;
            StructuralModels = structuralModels;
        }

        public ModelInfo Model { get; }
        public ImmutableArray<MemberModel> Members { get; }
        public ImmutableArray<PocoCloneModel> PocoCloneModels { get; }
        public ImmutableArray<StructuralModel> StructuralModels { get; }

        public bool Equals(PromotedModel? other)
        {
            return ReferenceEquals(this, other)
                || (
                    other is not null
                    && Model.Equals(other.Model)
                    && SequenceEqual(Members, other.Members)
                    && SequenceEqual(PocoCloneModels, other.PocoCloneModels)
                    && SequenceEqual(StructuralModels, other.StructuralModels)
                );
        }

        public override bool Equals(object? obj) => obj is PromotedModel other && Equals(other);

        public override int GetHashCode()
        {
            var hash = Model.GetHashCode();
            foreach (var member in Members)
            {
                hash = unchecked(hash * 31 + member.GetHashCode());
            }
            foreach (var poco in PocoCloneModels)
            {
                hash = unchecked(hash * 31 + poco.GetHashCode());
            }
            foreach (var structural in StructuralModels)
            {
                hash = unchecked(hash * 31 + structural.GetHashCode());
            }

            return hash;
        }
    }

    private static bool IsPromotedPartialType(
        INamedTypeSymbol type,
        CancellationToken cancellationToken
    )
    {
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                reference.GetSyntax(cancellationToken) is TypeDeclarationSyntax declaration
                && declaration.Modifiers.Any(SyntaxKind.PartialKeyword)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPromotablePartial(
        INamedTypeSymbol type,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (type.ContainingType is not null || type.Arity != 0 || type.IsAbstract)
        {
            return false;
        }

        if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct)
        {
            return false;
        }

        if (type.IsRefLikeType)
        {
            return false;
        }

        if (IsConfiglueModel(type, cancellationToken))
        {
            return false;
        }

        if (!IsPromotedPartialType(type, cancellationToken))
        {
            return false;
        }

        if (!IsAccessibleForGeneration(type))
        {
            return false;
        }

        if (type.SpecialType != SpecialType.None || IsFrameworkType(type))
        {
            return false;
        }

        if (
            SparseFragments.Generator.Shared.ModelConstructorBinding.AnalyzeStructural(
                type,
                cancellationToken
            )
                is null
            && SparseFragments.Generator.Shared.ModelConstructorBinding.AnalyzeRoot(
                type,
                cancellationToken
            )
                is null
        )
        {
            return false;
        }

        if (HasUnsupportedPocoMembers(type, cancellationToken))
        {
            return false;
        }

        if (
            !SparseFragments
                .Generator.Shared.SparseModelDiscovery.GetReadableProperties(
                    type,
                    cancellationToken
                )
                .Any()
        )
        {
            return false;
        }

        return true;
    }

    private static ImmutableArray<INamedTypeSymbol> CollectPromotedSymbols(
        ImmutableArray<SymbolMemberModel> members,
        CancellationToken cancellationToken
    )
    {
        var result = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var pending = new Stack<ITypeSymbol>();
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pending.Push(member.Property.Type);
        }

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = pending.Pop();
            if (type is INamedTypeSymbol named)
            {
                var collection = GetCollectionInfo(named);
                if (collection.CloneKind == CloneCollectionKind.Unsupported)
                {
                    if (
                        named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                        && named.TypeArguments.Length == 1
                        && named.TypeArguments[0] is INamedTypeSymbol underlying
                        && IsPromotablePartial(underlying, cancellationToken)
                        && seen.Add(underlying)
                    )
                    {
                        result.Add(underlying);
                        foreach (
                            var nested in GetMembers(underlying, cancellationToken)
                                .Select(static member => member.Property.Type)
                        )
                        {
                            pending.Push(nested);
                        }
                    }
                    else if (IsPromotablePartial(named, cancellationToken) && seen.Add(named))
                    {
                        result.Add(named);
                        foreach (
                            var nested in GetMembers(named, cancellationToken)
                                .Select(static member => member.Property.Type)
                        )
                        {
                            pending.Push(nested);
                        }
                    }
                }
            }

            foreach (var nested in UnwrapPromotedCollectionElements(type, cancellationToken))
            {
                pending.Push(nested);
            }
        }

        return result
            .ToImmutable()
            .Sort(
                static (left, right) =>
                    string.Compare(
                        left.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        right.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        System.StringComparison.Ordinal
                    )
            );
    }

    private static IEnumerable<ITypeSymbol> UnwrapPromotedCollectionElements(
        ITypeSymbol type,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (type is IArrayTypeSymbol array)
        {
            yield return array.ElementType;
            yield break;
        }

        if (type is INamedTypeSymbol named)
        {
            if (
                named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                && named.TypeArguments.Length == 1
            )
            {
                yield return named.TypeArguments[0];
                yield break;
            }

            var collection = GetCollectionInfo(named);
            if (collection.CloneKind != CloneCollectionKind.Unsupported)
            {
                if (collection.ElementType is not null)
                {
                    yield return collection.ElementType;
                }

                if (collection.ValueType is not null)
                {
                    yield return collection.ValueType;
                }
            }
        }
    }

    private static ImmutableArray<PromotedModel> CreatePromotedModels(
        ImmutableArray<SymbolMemberModel> members,
        CancellationToken cancellationToken
    )
    {
        var symbols = CollectPromotedSymbols(members, cancellationToken);
        var result = ImmutableArray.CreateBuilder<PromotedModel>(symbols.Length);
        foreach (var symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbolMembers = GetMembers(symbol, cancellationToken).ToImmutableArray();
            var memberModels = CreateMemberModels(symbolMembers, cancellationToken);
            var pocoCloneModels = GetPocoCloneTypes(symbolMembers, cancellationToken)
                .Select(pocoType => CreatePocoCloneModel(pocoType, cancellationToken))
                .ToImmutableArray();
            var structuralModels = CollectStructuralTypes(symbolMembers, cancellationToken)
                .Select(type => CreateStructuralModel(type, cancellationToken))
                .ToImmutableArray();
            var fullyQualified = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var modelInfo = CreateModelInfo(
                symbol,
                string.Empty,
                InitialSchemaVersion,
                cancellationToken
            );
            // Promoted children are not independent roots: they carry no model ID,
            // no registry participation, and no facade ownership semantics.
            modelInfo = modelInfo with
            {
                ModelId = fullyQualified,
                Version = InitialSchemaVersion,
            };
            result.Add(
                new PromotedModel(modelInfo, memberModels, pocoCloneModels, structuralModels)
            );
        }

        return result.ToImmutable();
    }

    private static string GetPromotedHintName(ModelInfo model, CancellationToken cancellationToken)
    {
        var fullyQualifiedName = model.FullyQualifiedName;
        return Sanitize(fullyQualifiedName, cancellationToken)
            + "_"
            + GetStableTypeHash(fullyQualifiedName, cancellationToken)
            + ".ConfigluePromoted.g.cs";
    }

    private static string BuildPromotedSource(
        PromotedModel promoted,
        bool hasJsonFragmentRegistry,
        bool hasMessagePackFragmentRegistry,
        bool hasJsonPatch,
        bool bclSetSupportsReadOnlySet,
        bool bclSetSupportsCapacity,
        CancellationToken cancellationToken
    )
    {
        var emitJsonConverter = hasJsonFragmentRegistry || hasJsonPatch;
        var model = promoted.Model;
        var members = promoted.Members;
        var pocoCloneModels = promoted.PocoCloneModels;
        var structuralModels = promoted.StructuralModels;
        var portableSetView =
            SparseFragments.Generator.Shared.SparseFragmentCoreEmitter.RequiresPortableSetView(
                bclSetSupportsReadOnlySet,
                members
                    .Select(static member => member.Collection.NamedTypeDefinition)
                    .Concat(
                        pocoCloneModels.SelectMany(static poco =>
                            poco.Members.Select(static member =>
                                member.Collection.NamedTypeDefinition
                            )
                        )
                    )
                    .Concat(
                        structuralModels.SelectMany(static structural =>
                            structural.Members.Select(static member =>
                                member.Collection.NamedTypeDefinition
                            )
                        )
                    )
            );
        string generatedType;
        if (model.IsStruct)
        {
            generatedType =
                (model.IsReadOnly ? "readonly " : "")
                + (model.IsRecord ? "partial record struct " : "partial struct ");
        }
        else
        {
            generatedType = model.IsRecord ? "partial record " : "partial class ";
        }
        var name = EscapeIdentifier(model.Name);
        var modelType = model.ModelTypeName;
        var code = new IndentedStringBuilder(cancellationToken);
        code.AppendLine("// <auto-generated />");
        code.AppendLine("#nullable enable");
        var hasNamespace = !model.IsGlobalNamespace;
        if (hasNamespace)
        {
            code.Append("namespace ").Append(model.Namespace).AppendLine();
            code.AppendLine("{");
            code.IndentOffset++;
        }

        code.Append(generatedType)
            .Append(name)
            .Append(" : global::Configlue.IConfiglueDeepCloneable<")
            .Append(modelType)
            .AppendLine(">");
        code.AppendLine("{");
        AppendModelSchema(code, modelType, modelType, InitialSchemaVersion, members);
        AppendFragmentSchema(code, modelType, modelType, InitialSchemaVersion, members);
        SparseFragments.Generator.Shared.SparseFragmentCoreEmitter.AppendRootProjectionConstructor(
            code,
            name,
            members.Select(member => ToSparseMember(member, portableSetView)).ToImmutableArray(),
            model.Constructor
        );
        FragmentCore.AppendDeepClone(
            code,
            modelType,
            members.Select(member => ToSparseMember(member, portableSetView)).ToImmutableArray(),
            !pocoCloneModels.IsEmpty,
            model.Constructor,
            !model.IsStruct
        );
        foreach (var poco in pocoCloneModels)
            FragmentCore.AppendPocoCloneHelper(
                code,
                poco.Model.ModelTypeName,
                poco.CloneHelperName,
                poco.Members.Select(member => ToSparseMember(member, portableSetView))
                    .ToImmutableArray(),
                poco.Model.Constructor
            );
        SparseFragments.Generator.Shared.SparseFragmentCoreEmitter.AppendCollectionCloneHelpers(
            code,
            portableSetView,
            bclSetSupportsCapacity
        );
        AppendFragment(
            code,
            modelType,
            members,
            ImmutableArray<PreviousModelInfo>.Empty,
            !model.IsStruct,
            !pocoCloneModels.IsEmpty,
            emitJsonConverter,
            hasMessagePackFragmentRegistry,
            hasJsonPatch,
            portableSetView: portableSetView,
            isRootModel: false,
            constructor: model.Constructor
        );
        AppendDetailsTree(code, modelType, members);
        if (!model.IsStruct)
        {
            AppendObservableModel(code, modelType, true, members);
        }

        AppendStructuralModels(
            code,
            structuralModels,
            !pocoCloneModels.IsEmpty,
            emitJsonConverter,
            hasMessagePackFragmentRegistry,
            hasJsonPatch,
            portableSetView
        );
        code.AppendLine("}");
        if (hasNamespace)
        {
            code.IndentOffset--;
            code.AppendLine("}");
        }

        return code.ToString();
    }

    private static ImmutableArray<GenerationResult> RenderPromoted(
        ImmutableArray<GenerationAnalysis> analyses,
        bool hasJsonFragmentRegistry,
        bool hasMessagePackFragmentRegistry,
        bool hasJsonPatch,
        bool bclSetSupportsReadOnlySet,
        bool bclSetSupportsCapacity,
        CancellationToken cancellationToken
    )
    {
        var explicitRoots = new HashSet<string>(
            analyses
                .Where(static analysis => analysis.Model.HasValue)
                .Select(static analysis => analysis.Model!.Value.FullyQualifiedName),
            System.StringComparer.Ordinal
        );

        var deduped = new SortedDictionary<string, PromotedModel>(System.StringComparer.Ordinal);
        var incompatible = new HashSet<string>(System.StringComparer.Ordinal);
        var results = ImmutableArray.CreateBuilder<GenerationResult>();
        foreach (var analysis in analyses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var promoted in analysis.PromotedModels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = promoted.Model.FullyQualifiedName;
                if (explicitRoots.Contains(key))
                {
                    continue;
                }

                if (deduped.TryGetValue(key, out var existing))
                {
                    if (!existing.Equals(promoted) && incompatible.Add(key))
                    {
                        results.Add(
                            new GenerationResult(
                                null,
                                null,
                                ImmutableArray.Create(
                                    GeneratorDiagnosticInfo.Create(
                                        IncompatiblePromotedModel,
                                        GeneratorLocationInfo.Create(null),
                                        promoted.Model.Name
                                    )
                                )
                            )
                        );
                    }

                    continue;
                }

                deduped.Add(key, promoted);
            }
        }

        foreach (var promoted in deduped.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (incompatible.Contains(promoted.Model.FullyQualifiedName))
            {
                continue;
            }

            var hint = GetPromotedHintName(promoted.Model, cancellationToken);
            var source = BuildPromotedSource(
                promoted,
                hasJsonFragmentRegistry,
                hasMessagePackFragmentRegistry,
                hasJsonPatch,
                bclSetSupportsReadOnlySet,
                bclSetSupportsCapacity,
                cancellationToken
            );
            results.Add(
                new GenerationResult(hint, source, ImmutableArray<GeneratorDiagnosticInfo>.Empty)
            );
        }

        return results.ToImmutable();
    }
}
