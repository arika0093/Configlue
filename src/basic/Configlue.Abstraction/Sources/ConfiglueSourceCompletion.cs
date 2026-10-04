using Configlue.CompilerServices;
using Configlue.Resources;

namespace Configlue.Sources;

/// <summary>
/// Completes provider source registration with shared logical identity and routing settings.
/// </summary>
/// <remarks>Advanced composition SPI used by provider registration helpers.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public static class ConfiglueSourceCompletion
{
    /// <summary>
    /// Creates a source using an explicit logical identity, falling back to a provider-derived one.
    /// </summary>
    /// <typeparam name="TFragment">The generated fragment type.</typeparam>
    /// <param name="reader">The source reader.</param>
    /// <param name="id">The caller-configured logical source ID, if any.</param>
    /// <param name="derivedId">The provider-derived logical source ID used when <paramref name="id"/> is null.</param>
    /// <param name="priority">Higher values are read first.</param>
    /// <param name="fallbackCondition">Read statuses that allow the next source to be tried.</param>
    /// <param name="physicalOrigin">Human-readable physical location metadata for diagnostics.</param>
    /// <param name="fixedResourceId">An explicit physical identity override shared by all operation contexts.</param>
    /// <param name="explicitOnly">Whether this source is excluded from ordinary inferred write routing.</param>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
    public static StateSource<TFragment> WithDerivedIdentity<TFragment>(
        ISourceReader<TFragment> reader,
        string? id,
        string derivedId,
        int priority,
        StateFallbackCondition fallbackCondition,
        string? physicalOrigin,
        ResourceId? fixedResourceId,
        bool explicitOnly = false
    )
        where TFragment : class, IConfiglueFragment<TFragment> =>
        new StateSource<TFragment>(
            id ?? derivedId,
            reader,
            new StateSourceOptions<TFragment>
            {
                Priority = priority,
                FallbackCondition = fallbackCondition,
                PhysicalOrigin = physicalOrigin,
                FixedResourceId = fixedResourceId,
                ExplicitOnly = explicitOnly,
            }
        );

    /// <summary>
    /// Creates a source using an explicit logical identity, falling back to an automatically
    /// generated opaque identity distinguished by a logical descriptor.
    /// </summary>
    /// <typeparam name="TFragment">The generated fragment type.</typeparam>
    /// <param name="reader">The source reader.</param>
    /// <param name="id">The caller-configured logical source ID, if any.</param>
    /// <param name="logicalDescriptor">The descriptor distinguishing registrations of the same reader instance.</param>
    /// <param name="priority">Higher values are read first.</param>
    /// <param name="fallbackCondition">Read statuses that allow the next source to be tried.</param>
    /// <param name="physicalOrigin">Human-readable physical location metadata for diagnostics.</param>
    /// <param name="fixedResourceId">An explicit physical identity override shared by all operation contexts.</param>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
    public static StateSource<TFragment> WithDescribedIdentity<TFragment>(
        ISourceReader<TFragment> reader,
        string? id,
        string? logicalDescriptor,
        int priority,
        StateFallbackCondition fallbackCondition,
        string? physicalOrigin,
        ResourceId? fixedResourceId
    )
        where TFragment : class, IConfiglueFragment<TFragment> =>
        id is { } explicitId
            ? new StateSource<TFragment>(
                explicitId,
                reader,
                new StateSourceOptions<TFragment>
                {
                    Priority = priority,
                    FallbackCondition = fallbackCondition,
                    PhysicalOrigin = physicalOrigin,
                    FixedResourceId = fixedResourceId,
                }
            )
            : new StateSource<TFragment>(
                reader,
                new StateSourceOptions<TFragment>
                {
                    Priority = priority,
                    FallbackCondition = fallbackCondition,
                    PhysicalOrigin = physicalOrigin,
                    FixedResourceId = fixedResourceId,
                    LogicalDescriptor = logicalDescriptor,
                }
            );
}
