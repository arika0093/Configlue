namespace Configlue;

/// <summary>Reads and saves a configuration value.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public interface IWritableState<T> : IReadOnlyState<T>
{
    /// <summary>Applies a generated sparse patch to the configured write source.</summary>
    /// <remarks>Throws a <see cref="StateConflictException"/> when higher-priority contributions prevent the patch from producing its requested effective values.</remarks>
    ValueTask<StateWriteReceipt> SaveAsync(
        IConfigluePatch patch,
        CancellationToken cancellationToken = default
    );
}
