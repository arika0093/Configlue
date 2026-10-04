using Configlue.Sources;

namespace Configlue.Resources;

/// <summary>Shared transport/orchestration for document section resources.</summary>
/// <remarks>
/// Internal plumbing only: resource identity, non-success propagation, pipeline materialization,
/// backup-recovery forwarding, conditional-write checks, batch-mutation composition, physical
/// schema resolution, and watcher delegation. Parsing, path syntax, encoding, trivia/DOM handling,
/// and document editing stay format-local. Operates only on <see cref="ResourceReadResult"/>,
/// <see cref="ResourceWriteRequest"/>, <see cref="ResourceWriteMutation"/>,
/// <see cref="ConfiglueResourceContext"/>, and resource capabilities; no generic document abstraction.
/// </remarks>
internal static class SectionResourceOrchestration
{
    public static string BuildBatchScope(string formatMoniker, string[] path) =>
        formatMoniker + "/" + string.Join("/", path.Select(Uri.EscapeDataString));

    public static bool TryGetResourceId(
        ResourceId? configuredResourceId,
        object? writer,
        object? reader,
        ConfiglueResourceContext context,
        out ResourceId resourceId
    )
    {
        if (configuredResourceId is { } configured)
        {
            resourceId = configured;
            return true;
        }

        if (writer.TryGetResourceId(context, out resourceId))
        {
            return true;
        }

        return reader.TryGetResourceId(context, out resourceId);
    }

    public static ResourceId GetResourceIdOrThrow(
        ResourceId? configuredResourceId,
        object? writer,
        object? reader,
        ConfiglueResourceContext context
    ) =>
        TryGetResourceId(configuredResourceId, writer, reader, context, out var resourceId)
            ? resourceId
            : throw new InvalidOperationException(
                "The underlying resource has no physical identity."
            );

    /// <summary>Maps a non-success physical result to the section view; returns false for success.</summary>
    public static bool TryPropagateNonSuccess(
        ResourceReadResult resource,
        out ResourceReadResult mapped
    )
    {
        if (resource.Status == StateReadStatus.Success)
        {
            mapped = default;
            return false;
        }

        mapped = resource.Status switch
        {
            StateReadStatus.NotFound => ResourceReadResult.NotFound(resource.Revision),
            StateReadStatus.Unavailable => ResourceReadResult.Unavailable(resource.Revision),
            StateReadStatus.InvalidPayload => ResourceReadResult.InvalidPayload(resource.Revision),
            _ => throw new InvalidOperationException("Unexpected non-success resource status."),
        };
        return true;
    }

    public static ValueTask<PipelineResourceReadResult> ReadPipelineFromSectionAsync(
        Func<ValueTask<ResourceReadResult>> readSectionAsync,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(readSectionAsync);
        return ReadPipelineFromSectionAsyncCore(readSectionAsync, cancellationToken);
    }

    private static async ValueTask<PipelineResourceReadResult> ReadPipelineFromSectionAsyncCore(
        Func<ValueTask<ResourceReadResult>> readSectionAsync,
        CancellationToken cancellationToken
    )
    {
        var result = await readSectionAsync().ConfigureAwait(false);
        return await PipelineResourceReader
            .FromMemoryAsync(result, cancellationToken)
            .ConfigureAwait(false);
    }

    public static bool GetAutomaticBackupRecoveryEnabled(IResourceReader reader) =>
        reader is IResourceBackupRecovery recovery && recovery.AutomaticBackupRecoveryEnabled;

    public static async ValueTask<ResourceReadResult?> TryRecoverLatestBackupAsync(
        IResourceReader reader,
        ConfiglueResourceContext context,
        string? expectedRevision,
        bool expectedMissing,
        Func<ResourceReadResult, CancellationToken, ValueTask<bool>> validate,
        Func<ResourceReadResult, ResourceReadResult> extractSection,
        Func<Exception, bool> isFormatError,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(validate);
        ArgumentNullException.ThrowIfNull(extractSection);
        ArgumentNullException.ThrowIfNull(isFormatError);

        if (
            reader is not IResourceBackupRecovery recovery
            || !recovery.AutomaticBackupRecoveryEnabled
        )
        {
            return null;
        }

        var restored = await recovery
            .TryRecoverLatestBackupAsync(
                context,
                expectedRevision,
                expectedMissing,
                async (candidate, token) =>
                {
                    ResourceReadResult section;
                    try
                    {
                        section = extractSection(candidate);
                    }
                    catch (Exception exception) when (isFormatError(exception))
                    {
                        return false;
                    }

                    return section.Status == StateReadStatus.Success
                        && await validate(section, token).ConfigureAwait(false);
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return restored is { } result ? extractSection(result) : null;
    }

    public static StateSchemaMetadata? ResolvePhysicalSchema(
        bool isRoot,
        StateSchemaMetadata? requestSchema,
        StateSchemaMetadata? containerSchema
    ) => isRoot ? requestSchema : containerSchema;

    public static StateSchemaMetadata? ResolveWriteSchema(
        bool isRoot,
        StateSchemaMetadata? requestSchema,
        StateSchemaMetadata? containerSchema,
        StateSchemaMetadata? currentSchema
    ) => isRoot ? requestSchema : containerSchema ?? currentSchema;

    public static void ThrowIfConditionalConflict(
        ResourceWriteRequest request,
        Func<ResourceReadResult, ResourceReadResult> extractSection,
        ResourceReadResult current
    )
    {
        if (request.Condition.IsNone)
        {
            return;
        }

        var section = extractSection(current);
        if (
            !request.Condition.IsSatisfiedBy(
                section.Revision,
                section.Status != StateReadStatus.NotFound
            )
        )
        {
            throw new StateConflictException("The section changed after it was read.");
        }
    }

    public static ResourceWriteMutation CreateSectionMutation(
        ResourceWriteRequest request,
        ConfiglueResourceContext context,
        string batchScope,
        StateSchemaMetadata? physicalSchema,
        Func<ResourceReadResult, ResourceReadResult> extractSection,
        Func<ResourceReadResult, ReadOnlyMemory<byte>> apply
    )
    {
        ArgumentNullException.ThrowIfNull(extractSection);
        ArgumentNullException.ThrowIfNull(apply);
        return new ResourceWriteMutation(
            request.Condition.IsMustNotExist ? RevisionCondition.None : request.Condition,
            physicalSchema,
            current =>
            {
                ThrowIfConditionalConflict(request, extractSection, current);
                return apply(current);
            },
            scope: batchScope,
            canCompose: true,
            context: context
        );
    }

    public static async ValueTask<StateWriteResult> WriteSectionWithMutationAsync(
        IResourceReader reader,
        IResourceWriter? writer,
        string readOnlyMessage,
        bool isRoot,
        StateSchemaMetadata? containerSchema,
        ResourceWriteRequest request,
        ConfiglueResourceContext context,
        ResourceWriteMutation mutation,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var activeWriter = writer ?? throw new NotSupportedException(readOnlyMessage);
        var current = await reader.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        var updatedDocument = mutation.Apply(current);
        return await activeWriter
            .WriteAsync(
                context,
                new ResourceWriteRequest(
                    updatedDocument,
                    Condition: RevisionCondition.FromRevision(current.Revision),
                    Schema: ResolveWriteSchema(
                        isRoot,
                        request.Schema,
                        containerSchema,
                        current.Schema
                    )
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public static void ThrowIfNotUpdatable(ResourceReadResult current, string formatName)
    {
        if (current.Status is not (StateReadStatus.Success or StateReadStatus.NotFound))
        {
            throw new IOException(
                $"The {formatName} resource is unavailable and cannot be updated safely."
            );
        }
    }

    public static async ValueTask WaitForChangeAsync(
        ISourceWatcher? watcher,
        Func<
            ConfiglueResourceContext,
            CancellationToken,
            ValueTask<ResourceReadResult>
        > readSectionAsync,
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(readSectionAsync);
        if (watcher is not null)
        {
            await watcher
                .WaitForChangeAsync(context, observedRevision, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await readSectionAsync(context, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current.Revision, observedRevision, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
