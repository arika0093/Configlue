using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Configlue.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class ConfiglueGenerator : IIncrementalGenerator
{
    private const string ModelAttributeName = "Configlue.ConfiglueModelAttribute";
    private const string MergeAttributeName = "Configlue.ConfiglueMergeAttribute";
    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
        SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
        SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly DiagnosticDescriptor MustBePartial = new(
        "CFG001", "Configlue model must be partial", "Model '{0}' must be declared partial", "Configlue", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor UnsupportedModel = new(
        "CFG002", "Unsupported Configlue model", "Model '{0}' must be a top-level, non-generic class or struct", "Configlue", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor MissingConstructor = new(
        "CFG003", "Model needs a public parameterless constructor", "Class model '{0}' must have a public parameterless constructor", "Configlue", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor UnsupportedRequired = new(
        "CFG004", "Required model members are unsupported", "Required member '{0}' cannot be omitted from a sparse fragment", "Configlue", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor UnsupportedMerge = new(
        "CFG005", "Unsupported merge mode", "Merge mode '{0}' is not supported for member '{1}'", "Configlue", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var models = context.SyntaxProvider.ForAttributeWithMetadataName(
            ModelAttributeName,
            static (node, _) => node is TypeDeclarationSyntax,
            static (attributeContext, _) => (INamedTypeSymbol)attributeContext.TargetSymbol);

        context.RegisterSourceOutput(models, static (productionContext, model) => Generate(productionContext, model));
    }

    private static void Generate(SourceProductionContext context, INamedTypeSymbol model)
    {
        var location = model.Locations.FirstOrDefault();
        var declaration = model.DeclaringSyntaxReferences
            .Select(static reference => reference.GetSyntax())
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();

        if (declaration is null || !declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            context.ReportDiagnostic(Diagnostic.Create(MustBePartial, location, model.Name));
            return;
        }

        if (model.ContainingType is not null || model.Arity != 0 || (model.TypeKind != TypeKind.Class && model.TypeKind != TypeKind.Struct))
        {
            context.ReportDiagnostic(Diagnostic.Create(UnsupportedModel, location, model.Name));
            return;
        }

        if (model.IsAbstract)
        {
            context.ReportDiagnostic(Diagnostic.Create(UnsupportedModel, location, model.Name));
            return;
        }

        if (model.TypeKind == TypeKind.Class && !model.InstanceConstructors.Any(static constructor =>
                constructor.DeclaredAccessibility == Accessibility.Public && constructor.Parameters.Length == 0))
        {
            context.ReportDiagnostic(Diagnostic.Create(MissingConstructor, location, model.Name));
            return;
        }

        var members = GetMembers(model).ToImmutableArray();
        var hasErrors = false;
        foreach (var member in members)
        {
            if (member.Property.IsRequired)
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedRequired, member.Property.Locations.FirstOrDefault(), member.Property.Name));
                hasErrors = true;
            }

            if (member.MergeMode == 1 && member.ChildModel is null)
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedMerge, member.Property.Locations.FirstOrDefault(), "Deep", member.Property.Name));
                hasErrors = true;
            }

            if ((member.MergeMode == 2 || member.MergeMode == 3) && member.Collection.Kind == CollectionKind.Unsupported)
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedMerge, member.Property.Locations.FirstOrDefault(),
                    member.MergeMode == 2 ? "Append" : "SetUnion", member.Property.Name));
                hasErrors = true;
            }
        }

        if (hasErrors)
        {
            return;
        }

        var source = BuildSource(model, members);
        var fileName = Sanitize(model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)) + ".Configlue.g.cs";
        context.AddSource(fileName, SourceText.From(source, Encoding.UTF8));
    }

    private static IEnumerable<MemberModel> GetMembers(INamedTypeSymbol model)
    {
        var hierarchy = new Stack<INamedTypeSymbol>();
        for (var current = model; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            hierarchy.Push(current);
        }

        var properties = new Dictionary<string, IPropertySymbol>(StringComparer.Ordinal);
        while (hierarchy.Count > 0)
        {
            foreach (var property in hierarchy.Pop().GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public ||
                    property.GetMethod?.DeclaredAccessibility != Accessibility.Public ||
                    property.SetMethod?.DeclaredAccessibility != Accessibility.Public)
                {
                    continue;
                }

                properties[property.Name] = property;
            }
        }

        var index = 0;
        foreach (var property in properties.Values.OrderBy(static property => property.Name, StringComparer.Ordinal))
        {
            var child = IsConfiglueModel(property.Type) ? (INamedTypeSymbol)property.Type : null;
            var mode = child is not null ? 1 : 0;
            var merge = property.GetAttributes().FirstOrDefault(static attribute =>
                attribute.AttributeClass?.ToDisplayString() == MergeAttributeName);
            if (merge?.ConstructorArguments.FirstOrDefault().Value is int requestedMode)
            {
                mode = requestedMode;
            }

            yield return new MemberModel(index++, property, child, mode, GetCollectionInfo(property.Type));
        }
    }

    private static bool IsConfiglueModel(ITypeSymbol type) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Class } named && named.GetAttributes().Any(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == ModelAttributeName);

    private static CollectionInfo GetCollectionInfo(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            return new CollectionInfo(CollectionKind.Array, array.ElementType, null);
        }

        if (type is not INamedTypeSymbol named || named.TypeArguments.Length != 1)
        {
            return CollectionInfo.Unsupported;
        }

        var elementType = named.TypeArguments[0];
        var definition = named.ConstructedFrom.ToDisplayString();
        var kind = definition switch
        {
            "System.Collections.Generic.List<T>" => CollectionKind.List,
            "System.Collections.Generic.IEnumerable<T>" or
            "System.Collections.Generic.IReadOnlyCollection<T>" or
            "System.Collections.Generic.IReadOnlyList<T>" => CollectionKind.Array,
            "System.Collections.Generic.HashSet<T>" or
            "System.Collections.Generic.ISet<T>" or
            "System.Collections.Generic.IReadOnlySet<T>" => CollectionKind.Set,
            _ => CollectionKind.Unsupported,
        };

        return new CollectionInfo(kind, elementType, named);
    }

    private static string BuildSource(INamedTypeSymbol model, ImmutableArray<MemberModel> members)
    {
        var modelType = NonNullableTypeName(model);
        var generatedType = model.TypeKind == TypeKind.Struct
            ? model.IsRecord ? "partial record struct " : "partial struct "
            : model.IsRecord ? "partial record class " : "partial class ";
        var name = EscapeIdentifier(model.Name);
        var modelId = GetModelId(model);
        var version = GetModelVersion(model);
        var code = new StringBuilder();
        code.AppendLine("// <auto-generated />");
        code.AppendLine("#nullable enable");
        if (!model.ContainingNamespace.IsGlobalNamespace)
        {
            code.Append("namespace ").Append(model.ContainingNamespace.ToDisplayString()).AppendLine(";");
        }

        code.Append(generatedType).Append(name).Append(" : global::Configlue.IConfiglueDeepCloneable<")
            .Append(modelType).AppendLine(">");
        code.AppendLine("{");
        AppendModelSchema(code, modelType, modelId, version, members);
        AppendDeepClone(code, modelType, members);
        AppendFragment(code, modelType, members);
        code.AppendLine("}");
        return code.ToString();
    }

    private static void AppendModelSchema(
        StringBuilder code,
        string modelType,
        string modelId,
        int version,
        ImmutableArray<MemberModel> members)
    {
        code.Append("    public static global::Configlue.ConfiglueModelSchema ConfiglueSchema { get; } = new(typeof(")
            .Append(modelType).Append("), ").Append(SymbolDisplay.FormatLiteral(modelId, true)).Append(", ").Append(version)
            .AppendLine(", new global::Configlue.ConfiglueMemberSchema[]");
        code.AppendLine("    {");
        foreach (var member in members)
        {
            code.Append("        new(").Append(member.Id).Append(", ")
                .Append(SymbolDisplay.FormatLiteral(member.Property.Name, true)).Append(", typeof(")
                .Append(NonNullableTypeName(member.Property.Type)).Append("), global::Configlue.MergeMode.")
                .Append(MergeModeName(member.MergeMode)).AppendLine("),");
        }

        code.AppendLine("    });");
    }

    private static void AppendDeepClone(StringBuilder code, string modelType, ImmutableArray<MemberModel> members)
    {
        code.Append("    public ").Append(modelType).AppendLine(" DeepClone() => new()");
        code.AppendLine("    {");
        foreach (var member in members)
        {
            code.Append("        ").Append(EscapeIdentifier(member.Property.Name)).Append(" = ")
                .Append(CloneModelExpression(member, "this." + EscapeIdentifier(member.Property.Name))).AppendLine(",");
        }

        code.AppendLine("    };");
    }

    private static void AppendFragment(StringBuilder code, string modelType, ImmutableArray<MemberModel> members)
    {
        code.AppendLine("    /// <summary>A sparse, presence-aware representation of this model.</summary>");
        code.AppendLine("    [global::System.Text.Json.Serialization.JsonConverter(typeof(FragmentJsonConverter))]");
        code.AppendLine("    public sealed class Fragment");
        code.AppendLine("    {");
        foreach (var member in members)
        {
            code.AppendLine("        [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]");
            code.Append("        public global::Configlue.Optional<").Append(FragmentValueType(member)).Append("> ")
                .Append(EscapeIdentifier(member.Property.Name)).AppendLine(" { get; init; }");
        }

        code.AppendLine();
        code.AppendLine("        /// <summary>Whether this fragment has no present members.</summary>");
        code.Append("        public bool IsEmpty => ").Append(members.Length == 0 ? "true" : string.Join(" && ", members.Select(member =>
            "!" + EscapeIdentifier(member.Property.Name) + ".IsPresent"))).AppendLine(";");
        code.AppendLine();
        AppendFromModel(code, modelType, members);
        AppendToModel(code, modelType, members);
        AppendMerge(code, members);
        AppendDiff(code, modelType, members);
        AppendFragmentClone(code, members);
        AppendPatchSupport(code, members);
        AppendJsonConverter(code, members);
        code.AppendLine("    }");
        AppendBuilder(code, members);
        AppendPatch(code, members);
    }

    private static void AppendFromModel(StringBuilder code, string modelType, ImmutableArray<MemberModel> members)
    {
        code.Append("        public static Fragment From(").Append(modelType).AppendLine(" value)");
        code.AppendLine("        {");
        code.AppendLine("            global::System.ArgumentNullException.ThrowIfNull(value);");
        code.AppendLine("            return new Fragment");
        code.AppendLine("            {");
        foreach (var member in members)
        {
            var access = "value." + EscapeIdentifier(member.Property.Name);
            var value = member.ChildModel is null
                ? access
                : $"({access} is null ? null : {NonNullableTypeName(member.ChildModel)}.Fragment.From({access}))";
            code.Append("                ").Append(EscapeIdentifier(member.Property.Name)).Append(" = global::Configlue.Optional<")
                .Append(FragmentValueType(member)).Append(">.Present(").Append(value).AppendLine("),");
        }

        code.AppendLine("            };");
        code.AppendLine("        }");
        code.AppendLine();
    }

    private static void AppendToModel(StringBuilder code, string modelType, ImmutableArray<MemberModel> members)
    {
        code.Append("        public ").Append(modelType).AppendLine(" ToModel()");
        code.AppendLine("        {");
        code.Append("            var defaults = new ").Append(modelType).AppendLine("();");
        code.Append("            return new ").Append(modelType).AppendLine();
        code.AppendLine("            {");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            var value = member.ChildModel is null
                ? name + ".Value!"
                : name + ".Value?.ToModel()!";
            code.Append("                ").Append(name).Append(" = ").Append(name).Append(".IsPresent ? ")
                .Append(value).Append(" : defaults.").Append(name).AppendLine(",");
        }

        code.AppendLine("            };");
        code.AppendLine("        }");
        code.AppendLine();
    }

    private static void AppendMerge(StringBuilder code, ImmutableArray<MemberModel> members)
    {
        code.AppendLine("        /// <summary>Merges a higher-priority fragment over this fragment.</summary>");
        code.AppendLine("        public Fragment Merge(Fragment higherPriority)");
        code.AppendLine("        {");
        code.AppendLine("            global::System.ArgumentNullException.ThrowIfNull(higherPriority);");
        code.AppendLine("            return new Fragment");
        code.AppendLine("            {");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            var lower = "this." + name;
            var higher = "higherPriority." + name;
            string expression;
            if (member.MergeMode == 1 && member.ChildModel is not null)
            {
                expression = $"{higher}.IsPresent ? global::Configlue.Optional<{FragmentValueType(member)}>.Present(({lower}.IsPresent && (object?){lower}.Value is not null && (object?){higher}.Value is not null) ? {lower}.Value!.Merge({higher}.Value!) : {higher}.Value) : {lower}";
            }
            else if (member.MergeMode is 2 or 3)
            {
                var merged = BuildCollectionMerge(member, lower + ".Value!", higher + ".Value!");
                expression = $"{higher}.IsPresent ? ({lower}.IsPresent && (object?){lower}.Value is not null && (object?){higher}.Value is not null ? global::Configlue.Optional<{FragmentValueType(member)}>.Present({merged}) : {higher}) : {lower}";
            }
            else
            {
                expression = $"{higher}.IsPresent ? {higher} : {lower}";
            }

            code.Append("                ").Append(name).Append(" = ").Append(expression).AppendLine(",");
        }

        code.AppendLine("            };");
        code.AppendLine("        }");
        code.AppendLine();
    }

    private static void AppendDiff(StringBuilder code, string modelType, ImmutableArray<MemberModel> members)
    {
        foreach (var member in members.Where(static member => member.ChildModel is not null))
        {
            var type = NonNullableTypeName(member.ChildModel!);
            var name = EscapeIdentifier(member.Property.Name);
            code.Append("        private static global::Configlue.Optional<").Append(FragmentValueType(member)).Append("> __Diff_")
                .Append(name).Append('(').Append(type).Append("? before, ").Append(type).AppendLine("? after)");
            code.AppendLine("        {");
            code.AppendLine("            if (global::System.Object.ReferenceEquals(before, after)) return default;");
            code.Append("            if (before is null || after is null) return global::Configlue.Optional<")
                .Append(FragmentValueType(member)).Append(">.Present(after is null ? null : ").Append(type).AppendLine(".Fragment.From(after));");
            code.Append("            var difference = ").Append(type).AppendLine(".Fragment.Diff(before, after);");
            code.Append("            return difference.IsEmpty ? default : global::Configlue.Optional<").Append(FragmentValueType(member))
                .AppendLine(">.Present(difference); ");
            code.AppendLine("        }");
        }

        code.AppendLine("        /// <summary>Creates a sparse semantic diff between two ordinary model values.</summary>");
        code.Append("        public static Fragment Diff(").Append(modelType).Append(" before, ").Append(modelType).AppendLine(" after)");
        code.AppendLine("        {");
        code.AppendLine("            global::System.ArgumentNullException.ThrowIfNull(before);");
        code.AppendLine("            global::System.ArgumentNullException.ThrowIfNull(after);");
        code.AppendLine("            return new Fragment");
        code.AppendLine("            {");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            var before = "before." + name;
            var after = "after." + name;
            var valueType = FragmentValueType(member);
            var condition = member.ChildModel is null
                ? $"global::Configlue.ConfiglueValueComparer.AreEqual({before}, {after}) ? default : global::Configlue.Optional<{valueType}>.Present({after})"
                : $"__Diff_{name}({before}, {after})";
            code.Append("                ").Append(name).Append(" = ").Append(condition).AppendLine(",");
        }

        code.AppendLine("            };");
        code.AppendLine("        }");
        code.AppendLine();
    }

    private static void AppendFragmentClone(StringBuilder code, ImmutableArray<MemberModel> members)
    {
        code.AppendLine("        /// <summary>Copies the fragment and its generated nested values.</summary>");
        code.AppendLine("        public Fragment DeepClone() => new()");
        code.AppendLine("        {");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            var type = FragmentValueType(member);
            var expression = CloneFragmentExpression(member, "this." + name + ".Value");
            code.Append("            ").Append(name).Append(" = this.").Append(name).Append(".IsPresent ? global::Configlue.Optional<")
                .Append(type).Append(">.Present(").Append(expression).Append(" ) : default,").AppendLine();
        }

        code.AppendLine("        };");
        code.AppendLine();
    }

    private static void AppendPatchSupport(StringBuilder code, ImmutableArray<MemberModel> members)
    {
        code.AppendLine("        /// <summary>Applies source-local set and unset operations to this fragment.</summary>");
        code.AppendLine("        public Fragment Apply(Patch patch)");
        code.AppendLine("        {");
        code.AppendLine("            global::System.ArgumentNullException.ThrowIfNull(patch);");
        code.AppendLine("            return new Fragment");
        code.AppendLine("            {");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            code.Append("                ").Append(name).Append(" = patch.").Append(name).Append(".Apply(this.").Append(name).AppendLine("),");
        }

        code.AppendLine("            };");
        code.AppendLine("        }");
        code.AppendLine();
        code.AppendLine("        /// <summary>Creates a mutable builder initialized from this fragment.</summary>");
        code.AppendLine("        public FragmentBuilder ToBuilder() => new(this);");
        code.AppendLine();
        code.AppendLine("        /// <summary>Creates a source-local set patch from all present members.</summary>");
        code.AppendLine("        public Patch ToPatch() => new(this);");
    }

    private static void AppendJsonConverter(StringBuilder code, ImmutableArray<MemberModel> members)
    {
        code.AppendLine("        /// <summary>Reads and writes sparse fragment properties without materializing absent values.</summary>");
        code.AppendLine("        public sealed class FragmentJsonConverter : global::System.Text.Json.Serialization.JsonConverter<Fragment>");
        code.AppendLine("        {");
        code.AppendLine("            public override Fragment Read(ref global::System.Text.Json.Utf8JsonReader reader, global::System.Type typeToConvert, global::System.Text.Json.JsonSerializerOptions options)");
        code.AppendLine("            {");
        code.AppendLine("                if (reader.TokenType != global::System.Text.Json.JsonTokenType.StartObject) throw new global::System.Text.Json.JsonException(\"A fragment must be a JSON object.\");");
        code.AppendLine("                var builder = new FragmentBuilder();");
        code.AppendLine("                while (reader.Read())");
        code.AppendLine("                {");
        code.AppendLine("                    if (reader.TokenType == global::System.Text.Json.JsonTokenType.EndObject) return builder.Build();");
        code.AppendLine("                    if (reader.TokenType != global::System.Text.Json.JsonTokenType.PropertyName) throw new global::System.Text.Json.JsonException(\"Expected a fragment property name.\");");
        code.AppendLine("                    var propertyName = reader.GetString();");
        code.AppendLine("                    if (!reader.Read()) throw new global::System.Text.Json.JsonException(\"Unexpected end of fragment.\");");
        if (members.Length > 0)
        {
            var first = true;
            foreach (var member in members)
            {
                var property = EscapeIdentifier(member.Property.Name);
                var wireName = GetJsonPropertyName(member.Property, out var explicitName);
                code.Append(first ? "                    if (" : "                    else if (")
                    .Append("Matches(propertyName, ").Append(SymbolDisplay.FormatLiteral(wireName, true)).Append(", ")
                    .Append(explicitName ? "false" : "true").AppendLine(", options))");
                code.Append("                        builder.").Append(property).Append(" = global::Configlue.Optional<").Append(FragmentValueType(member))
                    .Append(">.Present(global::System.Text.Json.JsonSerializer.Deserialize<").Append(FragmentValueType(member))
                    .AppendLine(">(ref reader, options));");
                first = false;
            }

            code.AppendLine("                    else reader.Skip();");
        }
        else
        {
            code.AppendLine("                    else reader.Skip();");
        }

        code.AppendLine("                }");
        code.AppendLine("                throw new global::System.Text.Json.JsonException(\"Unexpected end of fragment.\");");
        code.AppendLine("            }");
        code.AppendLine();
        code.AppendLine("            public override void Write(global::System.Text.Json.Utf8JsonWriter writer, Fragment value, global::System.Text.Json.JsonSerializerOptions options)");
        code.AppendLine("            {");
        code.AppendLine("                writer.WriteStartObject();");
        foreach (var member in members)
        {
            var property = EscapeIdentifier(member.Property.Name);
            var wireName = GetJsonPropertyName(member.Property, out var explicitName);
            code.Append("                if (value.").Append(property).AppendLine(".IsPresent)");
            code.AppendLine("                {");
            if (explicitName)
            {
                code.Append("                    writer.WritePropertyName(").Append(SymbolDisplay.FormatLiteral(wireName, true)).AppendLine(");");
            }
            else
            {
                code.Append("                    writer.WritePropertyName(options.PropertyNamingPolicy?.ConvertName(")
                    .Append(SymbolDisplay.FormatLiteral(wireName, true)).Append(") ?? ")
                    .Append(SymbolDisplay.FormatLiteral(wireName, true)).AppendLine(");");
            }

            code.Append("                    global::System.Text.Json.JsonSerializer.Serialize<").Append(FragmentValueType(member)).Append(">(writer, value.")
                .Append(property).AppendLine(".Value!, options);");
            code.AppendLine("                }");
        }

        code.AppendLine("                writer.WriteEndObject();");
        code.AppendLine("            }");
        code.AppendLine();
        code.AppendLine("            private static bool Matches(string? actual, string propertyName, bool useNamingPolicy, global::System.Text.Json.JsonSerializerOptions options)");
        code.AppendLine("            {");
        code.AppendLine("                if (actual is null) return false;");
        code.AppendLine("                var expected = useNamingPolicy ? options.PropertyNamingPolicy?.ConvertName(propertyName) ?? propertyName : propertyName;");
        code.AppendLine("                return global::System.String.Equals(actual, expected, options.PropertyNameCaseInsensitive ? global::System.StringComparison.OrdinalIgnoreCase : global::System.StringComparison.Ordinal);");
        code.AppendLine("            }");
        code.AppendLine("        }");
    }

    private static void AppendBuilder(StringBuilder code, ImmutableArray<MemberModel> members)
    {
        code.AppendLine("    /// <summary>A mutable builder for a generated fragment.</summary>");
        code.AppendLine("    public sealed class FragmentBuilder");
        code.AppendLine("    {");
        foreach (var member in members)
        {
            code.Append("        public global::Configlue.Optional<").Append(FragmentValueType(member)).Append("> ")
                .Append(EscapeIdentifier(member.Property.Name)).AppendLine(" { get; set; }");
        }

        code.AppendLine("        public FragmentBuilder() { }");
        code.AppendLine("        internal FragmentBuilder(Fragment fragment)");
        code.AppendLine("        {");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            code.Append("            ").Append(name).Append(" = fragment.").Append(name).AppendLine(";");
        }

        code.AppendLine("        }");
        code.AppendLine("        public Fragment Build() => new()");
        code.AppendLine("        {");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            code.Append("            ").Append(name).Append(" = ").Append(name).AppendLine(",");
        }

        code.AppendLine("        };");
        code.AppendLine("    }");
    }

    private static void AppendPatch(StringBuilder code, ImmutableArray<MemberModel> members)
    {
        code.AppendLine("    /// <summary>A source-local set/unset patch for generated fragment members.</summary>");
        code.AppendLine("    public sealed class Patch");
        code.AppendLine("    {");
        foreach (var member in members)
        {
            code.Append("        public global::Configlue.FragmentOperation<").Append(FragmentValueType(member)).Append("> ")
                .Append(EscapeIdentifier(member.Property.Name)).AppendLine(" { get; set; }");
        }

        code.AppendLine("        public Patch() { }");
        code.AppendLine("        internal Patch(Fragment fragment)");
        code.AppendLine("        {");
        foreach (var member in members)
        {
            var name = EscapeIdentifier(member.Property.Name);
            code.Append("            ").Append(name).Append(" = fragment.").Append(name).Append(".IsPresent ? global::Configlue.FragmentOperation<")
                .Append(FragmentValueType(member)).Append(">.Set(fragment.").Append(name).AppendLine(".Value) : default;");
        }

        code.AppendLine("        }");
        code.AppendLine("        public bool IsEmpty => ");
        code.Append("            ").Append(members.Length == 0 ? "true" : string.Join(" && ", members.Select(member =>
            EscapeIdentifier(member.Property.Name) + ".Kind == global::Configlue.FragmentOperationKind.Unchanged"))).AppendLine(";");
        code.AppendLine("    }");
    }

    private static string CloneModelExpression(MemberModel member, string access)
    {
        if (member.ChildModel is not null)
        {
            return $"{access} is null ? null! : {access}.DeepClone()";
        }

        if (member.Collection.Kind == CollectionKind.Unsupported)
        {
            return access;
        }

        var elementModel = IsConfiglueModel(member.Collection.ElementType);
        var enumerated = access;
        if (elementModel)
        {
            var elementType = TypeName(member.Collection.ElementType);
            enumerated = $"global::System.Linq.Enumerable.Select({access}, static item => item is null ? null : (({elementType})item).DeepClone())";
        }

        return member.Collection.Kind switch
        {
            CollectionKind.Array => $"global::System.Linq.Enumerable.ToArray({enumerated})",
            CollectionKind.List => $"new global::System.Collections.Generic.List<{TypeName(member.Collection.ElementType)}>({enumerated})",
            CollectionKind.Set => $"new global::System.Collections.Generic.HashSet<{TypeName(member.Collection.ElementType)}>({enumerated})",
            _ => access,
        };
    }

    private static string CloneFragmentExpression(MemberModel member, string access)
    {
        if (member.ChildModel is not null)
        {
            return $"{access}?.DeepClone()";
        }

        var elementModel = member.Collection.Kind != CollectionKind.Unsupported && IsConfiglueModel(member.Collection.ElementType);
        if (member.Collection.Kind == CollectionKind.Unsupported)
        {
            return access;
        }

        var enumerated = access;
        if (elementModel)
        {
            var elementType = TypeName(member.Collection.ElementType);
            enumerated = $"global::System.Linq.Enumerable.Select({access}!, static item => item is null ? null : (({elementType})item).DeepClone())";
        }

        var cloned = member.Collection.Kind switch
        {
            CollectionKind.Array => $"global::System.Linq.Enumerable.ToArray({enumerated})",
            CollectionKind.List => $"new global::System.Collections.Generic.List<{TypeName(member.Collection.ElementType)}>({enumerated})",
            CollectionKind.Set => $"new global::System.Collections.Generic.HashSet<{TypeName(member.Collection.ElementType)}>({enumerated})",
            _ => access,
        };
        return $"(object?){access} is null ? default : {cloned}";
    }

    private static string BuildCollectionMerge(MemberModel member, string lower, string higher)
    {
        var elementType = TypeName(member.Collection.ElementType);
        var combined = $"global::System.Linq.Enumerable.Concat({lower}, {higher})";
        if (member.MergeMode == 3)
        {
            combined = $"global::System.Linq.Enumerable.Distinct({combined})";
        }

        return member.Collection.Kind switch
        {
            CollectionKind.List => $"new global::System.Collections.Generic.List<{elementType}>({combined})",
            CollectionKind.Set => $"new global::System.Collections.Generic.HashSet<{elementType}>({combined})",
            _ => $"global::System.Linq.Enumerable.ToArray({combined})",
        };
    }

    private static string FragmentValueType(MemberModel member)
    {
        if (member.ChildModel is null)
        {
            return TypeName(member.Property.Type);
        }

        return NonNullableTypeName(member.ChildModel) + ".Fragment?";
    }

    private static string TypeName(ITypeSymbol type) => type.ToDisplayString(TypeFormat);

    private static string NonNullableTypeName(ITypeSymbol type) =>
        type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(TypeFormat);

    private static string MergeModeName(int mode) => mode switch
    {
        1 => "Deep",
        2 => "Append",
        3 => "SetUnion",
        _ => "Replace",
    };

    private static string GetModelId(INamedTypeSymbol model)
    {
        var attribute = model.GetAttributes().FirstOrDefault(static attribute => attribute.AttributeClass?.ToDisplayString() == ModelAttributeName);
        var id = attribute?.NamedArguments.FirstOrDefault(static pair => pair.Key == "Id").Value.Value as string;
        return id ?? model.ToDisplayString();
    }

    private static string GetJsonPropertyName(IPropertySymbol property, out bool isExplicit)
    {
        var attribute = property.GetAttributes().FirstOrDefault(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonPropertyNameAttribute");
        if (attribute?.ConstructorArguments.FirstOrDefault().Value is string configuredName)
        {
            isExplicit = true;
            return configuredName;
        }

        isExplicit = false;
        return property.Name;
    }

    private static int GetModelVersion(INamedTypeSymbol model)
    {
        var attribute = model.GetAttributes().FirstOrDefault(static attribute => attribute.AttributeClass?.ToDisplayString() == ModelAttributeName);
        return attribute?.ConstructorArguments.FirstOrDefault().Value is int version ? version : 1;
    }

    private static string EscapeIdentifier(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(identifier) != SyntaxKind.None
            ? "@" + identifier
            : identifier;

    private static string Sanitize(string identifier) => string.Concat(identifier.Select(static character =>
        char.IsLetterOrDigit(character) ? character : '_'));

    private sealed class MemberModel(int id, IPropertySymbol property, INamedTypeSymbol? childModel, int mergeMode, CollectionInfo collection)
    {
        public int Id { get; } = id;
        public IPropertySymbol Property { get; } = property;
        public INamedTypeSymbol? ChildModel { get; } = childModel;
        public int MergeMode { get; } = mergeMode;
        public CollectionInfo Collection { get; } = collection;
    }

    private sealed class CollectionInfo(CollectionKind kind, ITypeSymbol elementType, INamedTypeSymbol? namedType)
    {
        public CollectionKind Kind { get; } = kind;
        public ITypeSymbol ElementType { get; } = elementType;
        public INamedTypeSymbol? NamedType { get; } = namedType;
        public static CollectionInfo Unsupported { get; } = new(CollectionKind.Unsupported, null!, null);
    }

    private enum CollectionKind
    {
        Unsupported,
        Array,
        List,
        Set,
    }
}
