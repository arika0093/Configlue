using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// One routed logical source write with its physical resource grouping.
/// </summary>
internal readonly record struct PendingSourceWrite<TFragment>(
    StateSource<TFragment> Source,
    ISourceWriter<TFragment> Writer,
    StateWriteRequest<TFragment> Request,
    ResourceId? ResourceId,
    IResourceBatchWriter? BatchWriter,
    ResourceWriteMutation? Mutation
)
    where TFragment : class, IConfiglueFragment<TFragment>;

/// <summary>Write groups prepared without performing any physical write.</summary>
/// <remarks>
/// Each inner list executes as a single physical resource operation. Disposing releases
/// the prepared batch plans; no physical write has occurred while the holder is alive.
/// </remarks>
internal sealed class PreparedWriteGroups<TFragment>(
    List<List<PendingSourceWrite<TFragment>>> groups,
    DisposableBag owners
) : IDisposable
    where TFragment : class, IConfiglueFragment<TFragment>
{
    /// <summary>Each inner list executes as a single physical resource operation.</summary>
    public List<List<PendingSourceWrite<TFragment>>> Groups { get; } = groups;

    private DisposableBag? _owners = owners;

    public void Dispose()
    {
        Interlocked.Exchange(ref _owners, null)?.Dispose();
    }
}
