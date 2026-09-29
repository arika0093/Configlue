namespace Configlue;

/// <summary>Writes a generated patch to one selected logical source.</summary>
/// <typeparam name="TModel">The configuration model type.</typeparam>
public sealed class ConfiglueSourceHandle<TModel>
{
    private readonly IConfiglueSources<TModel> _sources;
    private readonly SourceKey<TModel> _sourceKey;

    internal ConfiglueSourceHandle(IConfiglueSources<TModel> sources, SourceKey<TModel> sourceKey)
    {
        _sources = sources;
        _sourceKey = sourceKey;
    }

    /// <summary>Applies only the Set and Unset operations in the patch to this source.</summary>
    public ValueTask<StateWriteReceipt> SaveAsync(
        IConfigluePatch patch,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(patch);
        return _sources.ApplyPatchesAsync(
            [new StateSourcePatch(_sourceKey.Id, patch)],
            cancellationToken
        );
    }

    /// <summary>Replaces this source contribution; unspecified generated patch members become Unset.</summary>
    public ValueTask<StateWriteReceipt> ReplaceAsync(
        IConfigluePatch patch,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (patch is not IConfiglueReplacementPatch replacementPatch)
        {
            throw new ArgumentException(
                "Source replacement requires a generated replacement-capable patch.",
                nameof(patch)
            );
        }

        return _sources.ApplyPatchesAsync(
            [new StateSourcePatch(_sourceKey.Id, replacementPatch.WithUnspecifiedMembersUnset())],
            cancellationToken
        );
    }
}
