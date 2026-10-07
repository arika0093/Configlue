namespace Configlue.Generator;

public sealed partial class ConfiglueGenerator
{
    // Configlue-owned JSON Patch bridge emission.
    //
    // The shared SparseJsonPatchEmitter stopped honoring its facade argument when
    // upstream internalized the bridge (SparseFragments #2): AppendFrom/ToJsonPatch
    // now hardcode global::SparseFragments.CompilerServices.SparseJsonPatchBridge,
    // which does not exist in compilations that reference only Configlue assemblies.
    // Configlue targets its own embedded runtime (global::Configlue.JsonPatchEngine
    // plus the ConfiglueJsonPatch facade), so this file replicates the previous
    // facade-honoring emission shape against the Configlue runtime. If upstream ever
    // gains a shared bridgeType parameter, these two methods should delegate again
    // and this file should shrink back to the dialect arguments.
    private static void AppendFromJsonPatch(
        IndentedStringBuilder code,
        string runtime,
        string facade,
        string optional,
        string jsonPrefix,
        string betweenCall
    )
    {
        var baselineType = optional + "<Fragment?>";
        var fromJsonPatch = jsonPrefix + "FromJsonPatch";
        code.AppendLineAt(
            2,
            "/// <summary>Imports an RFC 6902 JSON Patch document relative to a sparse baseline.</summary>"
        );
        code.AppendLineAt(
            2,
            "public static Patch "
                + fromJsonPatch
                + "("
                + baselineType
                + " baseline, System.ReadOnlyMemory<byte> jsonPatch, global::System.Text.Json.JsonSerializerOptions? options = null)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "var effective = __EffectiveOptions(options);");
        code.AppendLineAt(
            3,
            "var baselineNode = __SerializeFragmentToNode(baseline, effective, out var baselineIsAbsent);"
        );
        code.AppendLineAt(3, "var document = " + runtime + "." + facade + ".Parse(jsonPatch);");
        code.AppendLineAt(
            3,
            "var applied = "
                + runtime
                + ".JsonPatchEngine.Apply(baselineNode, baselineIsAbsent, document, __PropertyNameComparison(effective));"
        );
        code.AppendLineAt(3, baselineType + " result;");
        code.AppendLineAt(
            3,
            "if (applied.IsAbsent) { result = " + optional + "<Fragment?>.Missing; }"
        );
        code.AppendLineAt(
            3,
            "else if (applied.Node is null) { result = " + optional + "<Fragment?>.Present(null); }"
        );
        code.AppendLineAt(
            3,
            "else { result = "
                + optional
                + "<Fragment?>.Present(__DeserializeFragmentNode(applied.Node, effective)); }"
        );
        code.AppendLineAt(3, "return " + betweenCall + "(baseline, result);");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "/// <summary>Imports an RFC 6902 JSON Patch document relative to a present baseline.</summary>"
        );
        code.AppendLineAt(
            2,
            "public static Patch "
                + fromJsonPatch
                + "(Fragment baseline, System.ReadOnlyMemory<byte> jsonPatch, global::System.Text.Json.JsonSerializerOptions? options = null)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "if (baseline is null) throw new global::System.ArgumentNullException(nameof(baseline));"
        );
        code.AppendLineAt(
            3,
            "return "
                + fromJsonPatch
                + "("
                + optional
                + "<Fragment?>.Present(baseline), jsonPatch, options);"
        );
        code.AppendLineAt(2, "}");
    }

    private static void AppendToJsonPatch(
        IndentedStringBuilder code,
        string runtime,
        string facade,
        string optional,
        string jsonPrefix,
        string applyExpression
    )
    {
        _ = facade;
        var baselineType = optional + "<Fragment?>";
        var toJsonPatch = jsonPrefix + "ToJsonPatch";
        code.AppendLineAt(
            2,
            "/// <summary>Exports a semantically equivalent RFC 6902 JSON Patch document.</summary>"
        );
        code.AppendLineAt(
            2,
            "public System.ReadOnlyMemory<byte> "
                + toJsonPatch
                + "("
                + baselineType
                + " baseline, global::System.Text.Json.JsonSerializerOptions? options = null)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "var effective = __EffectiveOptions(options);");
        code.AppendLineAt(3, "var result = " + applyExpression + ";");
        code.AppendLineAt(
            3,
            "var beforeNode = __SerializeFragmentToNode(baseline, effective, out var beforeIsAbsent);"
        );
        code.AppendLineAt(
            3,
            "var afterNode = __SerializeFragmentToNode(result, effective, out var afterIsAbsent);"
        );
        code.AppendLineAt(
            3,
            "var document = "
                + runtime
                + ".JsonPatchEngine.Diff(beforeNode, beforeIsAbsent, afterNode, afterIsAbsent);"
        );
        code.AppendLineAt(3, "return " + runtime + ".JsonPatchEngine.Serialize(document);");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(
            2,
            "/// <summary>Exports a semantically equivalent RFC 6902 JSON Patch document.</summary>"
        );
        code.AppendLineAt(
            2,
            "public System.ReadOnlyMemory<byte> "
                + toJsonPatch
                + "(Fragment baseline, global::System.Text.Json.JsonSerializerOptions? options = null)"
        );
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "if (baseline is null) throw new global::System.ArgumentNullException(nameof(baseline));"
        );
        code.AppendLineAt(
            3,
            "return " + toJsonPatch + "(" + optional + "<Fragment?>.Present(baseline), options);"
        );
        code.AppendLineAt(2, "}");
    }
}
