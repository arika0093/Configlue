namespace Configlue;

/// <summary>The result of writing one source-local patch.</summary>
public readonly record struct StateSourceWriteResult(
    string SourceId,
    ResourceId? ResourceId,
    string? Revision);

/// <summary>The per-source results and physical write count of a multi-source patch operation.</summary>
public sealed class StateMultiWriteResult
{
    /// <summary>Creates a multi-source write result.</summary>
    public StateMultiWriteResult(IEnumerable<StateSourceWriteResult> sources, int physicalWriteCount)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (physicalWriteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalWriteCount));
        }

        var sourceResults = sources.ToArray();
        if (sourceResults.Any(static result => string.IsNullOrWhiteSpace(result.SourceId)) ||
            sourceResults.Select(static result => result.SourceId).Distinct(StringComparer.Ordinal).Count() != sourceResults.Length)
        {
            throw new ArgumentException("Source write results must have unique, non-empty source IDs.", nameof(sources));
        }

        if (physicalWriteCount > sourceResults.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalWriteCount), "Physical writes cannot exceed logical source writes.");
        }

        Sources = Array.AsReadOnly(sourceResults);
        PhysicalWriteCount = physicalWriteCount;
    }

    /// <summary>The result for each patched source.</summary>
    public IReadOnlyList<StateSourceWriteResult> Sources { get; }

    /// <summary>The number of physical resource writes performed.</summary>
    public int PhysicalWriteCount { get; }
}
