using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

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
                    && SparseFragments.Generator.Shared.SparseSequence.Equal(Members, other.Members)
                    && SparseFragments.Generator.Shared.SparseSequence.Equal(
                        PocoCloneModels,
                        other.PocoCloneModels
                    )
                    && SparseFragments.Generator.Shared.SparseSequence.Equal(
                        StructuralModels,
                        other.StructuralModels
                    )
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

    private static ImmutableArray<PromotedModel> CreatePromotedModels(
        ImmutableArray<SparseFragments.Generator.Shared.SparseSymbolMemberModel> members,
        CancellationToken cancellationToken
    )
    {
        var symbols = SparseFragments.Generator.Shared.SparsePromotedDiscovery.CollectPromotedTypes(
            members,
            SparseConfiguration,
            cancellationToken
        );
        var result = ImmutableArray.CreateBuilder<PromotedModel>(symbols.Length);
        foreach (var symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbolMembers = GetMembers(symbol, cancellationToken).ToImmutableArray();
            var memberModels = CreateMemberModels(symbolMembers, cancellationToken);
            var pocoCloneModels = SparseFragments
                .Generator.Shared.SparseModelDiscovery.GetPocoCloneTypes(
                    symbolMembers,
                    SparseConfiguration,
                    cancellationToken
                )
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
        bool bclSetSupportsReadOnlySet,
        bool bclSetSupportsCapacity,
        CancellationToken cancellationToken
    )
    {
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
            hasJsonFragmentRegistry,
            hasMessagePackFragmentRegistry,
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
            hasJsonFragmentRegistry,
            hasMessagePackFragmentRegistry,
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
