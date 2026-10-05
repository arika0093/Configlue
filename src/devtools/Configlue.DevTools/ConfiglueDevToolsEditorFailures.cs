namespace Configlue.DevTools;

using Configlue.CompilerServices;

/// <summary>
/// Failure classification and receipt shaping for the DevTools editor session's
/// commit/discard lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// Internal to the DevTools package. Maps core rebase/conflict/validation
/// failures into editor categories without duplicating core edit-session
/// semantics: the session owns lifecycle decisions while this collaborator owns
/// the category mapping and message shaping.
/// </para>
/// </remarks>
internal static class ConfiglueDevToolsEditorFailures
{
    /// <summary>
    /// Classifies an exception without hiding its semantic category.
    /// </summary>
    public static ConfiglueEditorFailureCategory Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            StateMultiWriteException => ConfiglueEditorFailureCategory.PartialWrite,
            Configlue.State.StateConflictException => ConfiglueEditorFailureCategory.Conflict,
            ConfiglueValidationException => ConfiglueEditorFailureCategory.Validation,
            ObjectDisposedException => ConfiglueEditorFailureCategory.Invalidated,
            InvalidOperationException invalid
                when invalid.Message.Contains(
                    "saving, rebasing, or disposed",
                    StringComparison.Ordinal
                ) => ConfiglueEditorFailureCategory.Invalidated,
            InvalidOperationException => ConfiglueEditorFailureCategory.Routing,
            System.Text.Json.JsonException => ConfiglueEditorFailureCategory.Parse,
            _ => ConfiglueEditorFailureCategory.Routing,
        };
    }

    /// <summary>Whether the exception maps to an editor failure category.</summary>
    public static bool IsMappable(Exception exception) =>
        exception
            is StateMultiWriteException
                or Configlue.State.StateConflictException
                or ConfiglueValidationException
                or ObjectDisposedException
                or InvalidOperationException;

    /// <summary>Whether the open failure means the state has no writable source.</summary>
    public static bool IsNoWriter(InvalidOperationException exception) =>
        exception.Message.Contains("No writable", StringComparison.Ordinal);

    /// <summary>Describes a mapped failure for editor display.</summary>
    public static string DescribeFailure(Exception exception)
    {
        var message = TrimMessage(exception.Message);
        if (exception is ConfiglueValidationException validation)
        {
            return $"Validation failed: {string.Join("; ", validation.Failures)}";
        }

        if (exception is StateMultiWriteException partial)
        {
            return DescribePartialWrite(partial);
        }

        return message;
    }

    /// <summary>Describes a multi-source partial write for editor display.</summary>
    public static string DescribePartialWrite(StateMultiWriteException exception)
    {
        string Sources(IReadOnlyList<SourceId> sources) =>
            sources.Count == 0
                ? "—"
                : string.Join(", ", sources.Select(static source => source.ToString()));
        return $"A multi-source write failed after partial completion. Completed {exception.Completed.Sources.Count} source(s); failed: {Sources(exception.FailedSourceIds)}; unattempted: {Sources(exception.UnattemptedSourceIds)}. Cause: {TrimMessage(exception.InnerException?.Message ?? exception.Message)}";
    }

    /// <summary>Shapes a core write receipt into editor metadata without flattening sources.</summary>
    public static ConfiglueEditorWriteReceipt ToWriteReceipt(
        StateWriteReceipt receipt,
        string stateName
    ) =>
        new(
            receipt
                .Sources.Select(static source => new ConfiglueEditorSourceWrite(
                    source.SourceId.ToString(),
                    source.ResourceId?.ToString(),
                    source.Revision
                ))
                .ToArray(),
            receipt.PhysicalWriteCount,
            receipt.PhysicalWriteCount <= 1,
            receipt.StateName,
            receipt.Revision
        );

    /// <summary>Receipt for a commit with no local changes (no writes performed).</summary>
    public static ConfiglueEditorWriteReceipt EmptyReceipt(string stateName) =>
        new([], 0, true, stateName, Revision: null);

    /// <summary>Trims a message for editor display.</summary>
    public static string TrimMessage(string message)
    {
        message = message.Trim();
        const int maxLength = 500;
        return message.Length <= maxLength ? message : message.Substring(0, maxLength) + "…";
    }
}
