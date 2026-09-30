namespace Configlue.Resources;

/// <summary>Context-free convenience and context-aware fallback resource operations.</summary>
public static class ResourceContextExtensions
{
    /// <summary>Reads the resource using the default context.</summary>
    public static ValueTask<ResourceReadResult> ReadAsync(
        this IResourceReader reader,
        CancellationToken cancellationToken = default
    )
    {
        if (reader is null)
        {
            throw new ArgumentNullException(nameof(reader));
        }
        return reader.ReadAsync(ConfiglueResourceContext.Default, cancellationToken);
    }

    /// <summary>Writes the resource using the default context.</summary>
    public static ValueTask<StateWriteResult> WriteAsync(
        this IResourceWriter writer,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (writer is null)
        {
            throw new ArgumentNullException(nameof(writer));
        }
        return writer.WriteAsync(ConfiglueResourceContext.Default, request, cancellationToken);
    }

    /// <summary>Recovers a subject-specific resource from a validated backup.</summary>
    public static ValueTask<ResourceReadResult?> TryRecoverLatestBackupAsync(
        this IResourceBackupRecovery recovery,
        ConfiglueResourceContext context,
        string? expectedRevision,
        bool expectedMissing,
        Func<ResourceReadResult, CancellationToken, ValueTask<bool>> validate,
        CancellationToken cancellationToken = default
    )
    {
        if (recovery is null)
        {
            throw new ArgumentNullException(nameof(recovery));
        }
        return recovery is IContextualResourceBackupRecovery contextualRecovery
            ? contextualRecovery.TryRecoverLatestBackupAsync(
                context,
                expectedRevision,
                expectedMissing,
                validate,
                cancellationToken
            )
            : recovery.TryRecoverLatestBackupAsync(
                expectedRevision,
                expectedMissing,
                validate,
                cancellationToken
            );
    }

    /// <summary>Gets the physical identity used for an operation on one subject.</summary>
    public static ResourceId GetResourceId(
        this IResourceIdentity identity,
        ConfiglueResourceContext context
    )
    {
        if (identity is null)
        {
            throw new ArgumentNullException(nameof(identity));
        }
        return identity is IContextualResourceIdentity contextualIdentity
            ? contextualIdentity.GetResourceId(context)
            : identity.ResourceId;
    }

    /// <summary>Gets the physical identity used for a batch mutation on one subject.</summary>
    public static ResourceId GetResourceId(
        this IResourceBatchParticipant participant,
        ConfiglueResourceContext context
    )
    {
        if (participant is null)
        {
            throw new ArgumentNullException(nameof(participant));
        }

        return participant is IContextualResourceBatchParticipant contextualParticipant
            ? contextualParticipant.GetResourceId(context)
            : participant.ResourceId;
    }

    /// <summary>Tries to get the physical identity used for an operation on one subject.</summary>
    public static bool TryGetResourceId(
        this IResourceIdentity identity,
        ConfiglueResourceContext context,
        out ResourceId resourceId
    )
    {
        if (identity is null)
        {
            throw new ArgumentNullException(nameof(identity));
        }
        if (identity is ITryContextualResourceIdentity tryContextualIdentity)
        {
            return tryContextualIdentity.TryGetResourceId(context, out resourceId);
        }

        if (identity is IContextualResourceIdentity contextualIdentity)
        {
            resourceId = contextualIdentity.GetResourceId(context);
            return true;
        }

        resourceId = identity.ResourceId;
        return true;
    }

    /// <summary>Creates a deferred mutation for one logical subject.</summary>
    public static ResourceWriteMutation CreateMutation(
        this IResourceBatchParticipant participant,
        ConfiglueResourceContext context,
        ResourceWriteRequest request
    )
    {
        if (participant is null)
        {
            throw new ArgumentNullException(nameof(participant));
        }
        return participant is IContextualResourceBatchParticipant contextualParticipant
            ? contextualParticipant.CreateMutation(context, request)
            : participant.CreateMutation(request).WithContext(context);
    }
}
