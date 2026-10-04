namespace Configlue.Resources;

/// <summary>Context-aware resource operations with portable fallback behavior.</summary>
/// <remarks>Provider/runtime plumbing: normalizes operation contexts and forwards optional capabilities.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public static class ResourceContextExtensions
{
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
        context = ConfiglueResourceContext.Normalize(context);
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
        context = ConfiglueResourceContext.Normalize(context);
        return RequireResourceId(identity.GetResourceId(context));
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

        context = ConfiglueResourceContext.Normalize(context);
        return RequireResourceId(participant.GetResourceId(context));
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
        context = ConfiglueResourceContext.Normalize(context);
        resourceId = RequireResourceId(identity.GetResourceId(context));
        return true;
    }

    /// <summary>Tries to get an optional physical identity for one operation context.</summary>
    public static bool TryGetResourceId(
        this ITryResourceIdentity identity,
        ConfiglueResourceContext context,
        out ResourceId resourceId
    )
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!identity.TryGetResourceId(context, out resourceId))
        {
            resourceId = default;
            return false;
        }

        resourceId = RequireResourceId(resourceId);
        return true;
    }

    /// <summary>Tries to resolve an identity exposed by either the required or optional identity contract.</summary>
    /// <remarks>Runtime/provider plumbing for duck-typed identity forwarding. Hidden from ordinary completion
    /// because the extension applies to every object.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static bool TryGetResourceId(
        this object? resource,
        ConfiglueResourceContext context,
        out ResourceId resourceId
    )
    {
        switch (resource)
        {
            case IResourceIdentity identity:
                resourceId = RequireResourceId(identity.GetResourceId(context));
                return true;
            case ITryResourceIdentity optionalIdentity:
                return TryGetResourceId(optionalIdentity, context, out resourceId);
            default:
                resourceId = default;
                return false;
        }
    }

    private static ResourceId RequireResourceId(ResourceId resourceId) =>
        !resourceId.IsDefault
            ? resourceId
            : throw new InvalidOperationException(
                "A resource identity provider returned the default ResourceId, which has no identity."
            );

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
        context = ConfiglueResourceContext.Normalize(context);
        return participant.CreateMutation(context, request);
    }
}
