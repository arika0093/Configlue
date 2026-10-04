namespace Configlue;

/// <summary>
/// Central redaction policy for members marked with <see cref="SecretValueAttribute"/>.
/// </summary>
/// <remarks>
/// <para>
/// Generic diagnostics and tooling must route value formatting through this helper instead of
/// repeating string checks. Typed application access via <c>.Value</c> still returns the real
/// value; only generic display, logging, diagnostic, and transport surfaces are redacted.
/// </para>
/// <para>
/// Tooling may expose safe metadata such as member path, presence, effective source,
/// editability, whether the member is secret, and source contribution state. It must never
/// place secret plaintext into serialized payloads, editor models, hover text, hints, history,
/// clipboard strings, or diagnostic history. Editing uses a separate explicit secret-change
/// flow rather than a fake editable <c>"********"</c> value.
/// </para>
/// <para>Advanced diagnostics vocabulary.</para>
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public static class ConfiglueSecrets
{
    /// <summary>The vendor extension name carrying sensitivity in generated JSON Schema.</summary>
    public const string JsonSchemaExtensionName = "x-configlue-secret";

    /// <summary>The display placeholder used instead of a secret value in generic surfaces.</summary>
    public const string RedactedText = "********";

    /// <summary>Formats a value for generic display, redacting secrets.</summary>
    /// <param name="value">The value to format.</param>
    /// <param name="isSecret">Whether the value belongs to a sensitive member subtree.</param>
    /// <returns>The redacted placeholder for secrets, otherwise the value text.</returns>
    public static string FormatValue(object? value, bool isSecret) =>
        isSecret ? RedactedText : value?.ToString() ?? string.Empty;

    /// <summary>Formats a contributed value for per-source display, redacting secrets.</summary>
    /// <param name="value">The contributed value.</param>
    /// <param name="isSecret">Whether the contribution belongs to a sensitive member subtree.</param>
    /// <param name="isPresent">Whether the source presently contributes a value.</param>
    /// <returns>The redacted placeholder for present secrets, otherwise the value text.</returns>
    public static string FormatContribution(object? value, bool isSecret, bool isPresent) =>
        isSecret && isPresent ? RedactedText : value?.ToString() ?? string.Empty;

    /// <summary>Whether a member schema is directly marked as sensitive.</summary>
    /// <param name="member">The member metadata.</param>
    /// <returns>Whether the member carries secret metadata.</returns>
    public static bool IsSensitive(ConfiglueMemberSchema member) => member.IsSecret;

    /// <summary>
    /// Whether a generated member path resolves through a sensitive member.
    /// </summary>
    /// <param name="path">The generated member path.</param>
    /// <returns>
    /// True when the leaf or any ancestor member is marked secret, so nested subtrees and
    /// collection elements inherit sensitivity without per-host rules and without reflection.
    /// </returns>
    public static bool IsSensitive(Configlue.CompilerServices.ConfiglueMemberPath path) =>
        path.IsSecret();

    /// <summary>
    /// Redacts occurrences of a known secret plaintext inside an otherwise free-form message.
    /// </summary>
    /// <param name="message">The message to sanitize.</param>
    /// <param name="secretValue">The secret value whose plaintext must not appear.</param>
    /// <param name="isSecret">Whether the message belongs to a sensitive member subtree.</param>
    /// <returns>The message with secret plaintext replaced by the redacted placeholder.</returns>
    /// <remarks>
    /// Framework-generated validation and error formatting never embeds values directly; this
    /// covers validator and data-annotation messages that echo the rejected value.
    /// </remarks>
    public static string RedactMessage(string message, object? secretValue, bool isSecret)
    {
        if (!isSecret || string.IsNullOrEmpty(message) || secretValue is null)
        {
            return message;
        }

        var plaintext = secretValue as string ?? secretValue.ToString();
        if (string.IsNullOrEmpty(plaintext) || plaintext.Length < 1)
        {
            return message;
        }

        return message.IndexOf(plaintext, StringComparison.Ordinal) >= 0
            ? message.Replace(plaintext, RedactedText)
            : message;
    }
}
