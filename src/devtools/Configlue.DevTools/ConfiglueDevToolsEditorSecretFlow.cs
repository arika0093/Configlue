using System.Text.Json;
using System.Text.Json.Nodes;
using Configlue.CompilerServices;

namespace Configlue.DevTools;

/// <summary>
/// Explicit secret mutation handling behind the DevTools editor session.
/// </summary>
/// <remarks>
/// <para>
/// Internal to the DevTools package. Secrets never enter the Monaco text model:
/// placeholders stay placeholders and real changes arrive only through the
/// explicit secret flow. Plaintext lives only in method parameters and the owned
/// draft; it is never stored, logged, or placed into Monaco undo history,
/// storage, hovers, or the initial render. The redacted placeholder is never a
/// valid secret value and can never be persisted as one.
/// </para>
/// </remarks>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "SonarAnalyzer.CSharp",
    "S2743",
    Justification = "The options instance is stateless configuration shared across all closed model types by design."
)]
internal sealed class ConfiglueDevToolsEditorSecretFlow<TModel>
    where TModel : IConfiglueFacadeModel<TModel>
{
    private static readonly JsonSerializerOptions StrictModelOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    private readonly ConfiglueModelSchema _schema;
    private readonly JsonNamingPolicy? _namingPolicy;

    public ConfiglueDevToolsEditorSecretFlow(
        ConfiglueModelSchema schema,
        JsonNamingPolicy? namingPolicy
    )
    {
        _schema = schema;
        _namingPolicy = namingPolicy;
    }

    /// <summary>
    /// Validates secret placeholders inside a normalized draft against the baseline.
    /// </summary>
    public void CheckDraft(
        JsonObject normalizedDraft,
        JsonObject normalizedBaseline,
        out List<string> plaintextSecrets,
        out List<string> deletedPlaceholders
    )
    {
        plaintextSecrets = [];
        deletedPlaceholders = [];
        ConfiglueEditorSemanticJson.ValidateDraftSecrets(
            _schema,
            normalizedDraft,
            normalizedBaseline,
            prefix: string.Empty,
            ancestorSecret: false,
            plaintextSecrets,
            deletedPlaceholders
        );
        plaintextSecrets.Sort(StringComparer.Ordinal);
        deletedPlaceholders.Sort(StringComparer.Ordinal);
    }

    /// <summary>Error lines for plaintext smuggled into the text draft.</summary>
    public static string[] DraftPlaintextErrors(IReadOnlyList<string> plaintextSecrets) =>
        plaintextSecrets
            .Select(static path =>
                $"Secret '{path}' must stay '{ConfiglueSecrets.RedactedText}' in the editor; use Change secret instead."
            )
            .ToArray();

    /// <summary>Error lines for deleted secret placeholders.</summary>
    public static string[] DeletedPlaceholderErrors(IReadOnlyList<string> deletedPlaceholders) =>
        deletedPlaceholders
            .Select(static path =>
                $"Secret placeholder '{path}' is a protected read-only range; restore the '{ConfiglueSecrets.RedactedText}' line or discard the draft."
            )
            .ToArray();

    /// <summary>
    /// Whether the desired value introduces a secret difference outside the
    /// explicit secret-change flow.
    /// </summary>
    public bool IsSmuggled(
        TModel before,
        TModel desired,
        Func<string, bool> isOverride,
        out string diffPath
    )
    {
        var differs = ConfiglueEditorSemanticJson.SecretSubtreeDiffers(
            _schema,
            before,
            desired,
            string.Empty,
            false,
            out diffPath
        );
        return differs && !isOverride(diffPath);
    }

    /// <summary>
    /// Validates the member path for the explicit secret flow without touching plaintext.
    /// </summary>
    public bool TryResolvePath(string memberPath, out ConfiglueMemberPath path, out string error)
    {
        try
        {
            path = ConfiglueMemberPath.FromNames(_schema, memberPath);
        }
        catch (ArgumentException exception)
        {
            path = default!;
            error =
                $"Unknown member path '{memberPath}': {ConfiglueDevToolsEditorFailures.TrimMessage(exception.Message)}";
            return false;
        }

        if (!path.IsSecret())
        {
            error = $"Member '{memberPath}' is not a secret; edit it in the Monaco draft instead.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Applies an explicit secret change to the current value. Plaintext is scoped
    /// to this call and the returned draft; it is never retained here.
    /// </summary>
    public bool TrySetValue(
        TModel current,
        string memberPath,
        string plaintext,
        out TModel desired,
        out ConfiglueEditorFailureCategory category,
        out string error
    )
    {
        if (string.Equals(plaintext, ConfiglueSecrets.RedactedText, StringComparison.Ordinal))
        {
            desired = current;
            category = ConfiglueEditorFailureCategory.Secret;
            error = "The redacted placeholder is never a valid secret value.";
            return false;
        }

        try
        {
            desired = SetSecretValue(current, memberPath, plaintext);
            category = ConfiglueEditorFailureCategory.None;
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
            when (exception is InvalidOperationException
                || exception is JsonException
                || exception is ArgumentException
            )
        {
            desired = current;
            category =
                exception is ArgumentException
                    ? ConfiglueEditorFailureCategory.Schema
                    : ConfiglueEditorFailureCategory.Secret;
            error = ConfiglueDevToolsEditorFailures.TrimMessage(exception.Message);
            return false;
        }
    }

    private TModel SetSecretValue(TModel current, string memberPath, string plaintext)
    {
        var node =
            JsonSerializer.SerializeToNode(current) as JsonObject
            ?? throw new InvalidOperationException(
                $"Secret '{memberPath}' cannot be addressed on a non-object model value."
            );
        var parts = memberPath.Split('.');
        var schema = _schema;
        var target = node;
        for (var index = 0; index < parts.Length; index++)
        {
            ConfiglueMemberSchema? found = null;
            foreach (var member in schema.Members)
            {
                if (
                    !member.IsDefault
                    && string.Equals(member.Name, parts[index], StringComparison.Ordinal)
                )
                {
                    found = member;
                    break;
                }
            }

            if (found is null)
            {
                throw new ArgumentException(
                    $"Property path '{memberPath}' contains unknown member '{parts[index]}'.",
                    nameof(memberPath)
                );
            }

            var key = ResolveNodeKey(target, found.Value, _namingPolicy);
            if (index == parts.Length - 1)
            {
                if (IsCollection(found.Value))
                {
                    throw new InvalidOperationException(
                        $"Secret '{memberPath}' is a collection; replace it through the Monaco draft's normal merge semantics instead."
                    );
                }

                target[key] = plaintext;
                break;
            }

            if (target[key] is not JsonObject child)
            {
                throw new InvalidOperationException(
                    $"Secret '{memberPath}' traverses non-object member '{found.Value.Name}'."
                );
            }

            target = child;
            schema =
                found.Value.NestedSchemaFactory?.Invoke()
                ?? throw new InvalidOperationException(
                    $"Secret '{memberPath}' continues through non-nested member '{found.Value.Name}'."
                );
        }

        return JsonSerializer.Deserialize<TModel>(node.ToJsonString(), StrictModelOptions)
            ?? throw new InvalidOperationException(
                "The secret change did not produce a model value."
            );
    }

    private static string ResolveNodeKey(
        JsonObject node,
        ConfiglueMemberSchema member,
        JsonNamingPolicy? namingPolicy
    )
    {
        if (node.ContainsKey(member.Name))
        {
            return member.Name;
        }

        string? converted = null;
        try
        {
            converted = namingPolicy?.ConvertName(member.Name);
        }
        catch (Exception)
        {
            converted = null;
        }

        if (converted is not null && node.ContainsKey(converted))
        {
            return converted;
        }

        foreach (var (candidate, _) in node)
        {
            if (string.Equals(candidate, member.Name, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }

            if (
                converted is not null
                && string.Equals(candidate, converted, StringComparison.OrdinalIgnoreCase)
            )
            {
                return candidate;
            }
        }

        return member.Name;
    }

    private static bool IsCollection(ConfiglueMemberSchema member)
    {
        try
        {
            var type = Nullable.GetUnderlyingType(member.ValueType) ?? member.ValueType;
            return type != typeof(string)
                && typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
