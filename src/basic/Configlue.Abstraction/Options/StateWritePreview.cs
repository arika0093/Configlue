namespace Configlue;

/// <summary>A dry-run description of the physical writes a desired effective model would require.</summary>
/// <remarks>
/// Computed without mutating any source or resource. A preview is advisory: concurrent changes
/// between the preview and the subsequent commit can still conflict or reroute the write.
/// </remarks>
public sealed class StateWritePreview
{
    /// <summary>A preview for a desired model that requires no physical writes.</summary>
    public static StateWritePreview Empty { get; } = new(0, true);

    /// <summary>Creates a write preview.</summary>
    /// <param name="physicalWriteCount">The number of physical resource operations the write would perform.</param>
    /// <param name="isEmpty">Whether the desired model already matches the effective state.</param>
    public StateWritePreview(int physicalWriteCount, bool isEmpty)
    {
        if (physicalWriteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalWriteCount));
        }

        if (isEmpty && physicalWriteCount != 0)
        {
            throw new ArgumentException(
                "An empty preview cannot require physical writes.",
                nameof(physicalWriteCount)
            );
        }

        PhysicalWriteCount = physicalWriteCount;
        IsEmpty = isEmpty;
    }

    /// <summary>The number of physical resource operations the write would perform.</summary>
    /// <remarks>
    /// Logical source writes that share one physical <c>ResourceId</c> and batch atomically
    /// count as a single physical write.
    /// </remarks>
    public int PhysicalWriteCount { get; }

    /// <summary>Whether the desired model already matches the effective state.</summary>
    public bool IsEmpty { get; }

    /// <summary>
    /// Whether the write can be executed atomically: no write is needed, or all writes group
    /// into a single physical resource operation.
    /// </summary>
    public bool IsAtomic => PhysicalWriteCount <= 1;
}
