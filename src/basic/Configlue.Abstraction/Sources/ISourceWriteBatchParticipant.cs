using Configlue.State;

namespace Configlue.Sources;

/// <summary>Asynchronously prepares a typed source write for batching with sibling sources.</summary>
/// <remarks>Advanced provider SPI for batch-capable sources.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IAsyncSourceWriteBatchParticipant<T>
{
    /// <summary>Creates the physical resource mutation for this typed write when batching is supported.</summary>
    ValueTask<StateWriteBatchPlan?> TryCreateBatchWriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    );
}
