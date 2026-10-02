namespace Configlue;

/// <summary>The outcome of committing an edit session, including the effective state the commit established.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public sealed class StateCommitResult<T>
{
    /// <summary>Creates a commit result.</summary>
    /// <param name="receipt">The receipt describing the logical and physical writes performed.</param>
    /// <param name="committedSnapshot">The effective state observed after the commit.</param>
    public StateCommitResult(StateWriteReceipt receipt, StateSnapshot<T> committedSnapshot)
        : this(receipt, committedSnapshot, upstreamGeneration: null) { }

    internal StateCommitResult(
        StateWriteReceipt receipt,
        StateSnapshot<T> committedSnapshot,
        long? upstreamGeneration
    )
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(committedSnapshot);
        Receipt = receipt;
        CommittedSnapshot = committedSnapshot;
        UpstreamGeneration = upstreamGeneration;
    }

    /// <summary>The receipt describing the logical and physical writes performed.</summary>
    public StateWriteReceipt Receipt { get; }

    /// <summary>The effective state observed after the commit, used to advance session baselines.</summary>
    public StateSnapshot<T> CommittedSnapshot { get; }

    internal long? UpstreamGeneration { get; }
}
