using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// Collects migration-only sources for one generated model. Sources registered here are
/// constructed through the same provider/source composition SPI as active runtime sources
/// (for example <c>sources.FromYamlFile(...)</c>) but never participate in normal runtime
/// resolution, watching, provenance, or inferred writes. Migration operations
/// (<c>MigrateSourceAsync</c>, <c>MigrateSourcesToTargetsAsync</c>, <c>AdoptLegacyAsync</c>)
/// may read them by their logical source IDs.
/// </summary>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
/// <remarks>
/// Representation/storage migration stays fragment-centric: a migration-only source supplies a
/// semantic fragment through its own Resource, Codec, Transformers, and schema dispatcher, and
/// the migration pipeline converts that fragment into the target representation. No second
/// provider ecosystem is introduced; provider helpers registered here are the same helpers used
/// for active sources.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class ConfiglueStorageMigrationBuilder<TModel>
    where TModel : IConfiglueFacadeModel<TModel>
{
    private readonly ConfiglueSourceSetBuilder<TModel> _sources = new();

    /// <summary>
    /// Registers migration-only inputs using the same provider helpers as active sources.
    /// </summary>
    public ConfiglueStorageMigrationBuilder<TModel> From(
        Action<ConfiglueSourceSetBuilder<TModel>> configure
    )
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_sources);
        return this;
    }

    /// <summary>
    /// Registers migration-only definitions for the target side of a representation migration.
    /// Targets of a migration run are resolved by logical source ID from the active runtime
    /// sources first and from these migration-only definitions second; registering a target here
    /// keeps it out of normal resolution while still allowing verified migration writes.
    /// </summary>
    public ConfiglueStorageMigrationBuilder<TModel> To(
        Action<ConfiglueSourceSetBuilder<TModel>> configure
    ) => From(configure);

    internal bool IsEmpty => !_sources.HasRegistrations;

    internal void CopyFrom(ConfiglueStorageMigrationBuilder<TModel> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _sources.CopyFrom(source._sources);
    }

    internal StateSourceSet<TFragment>? Build<TFragment>(
        ConfiglueModelSchema modelSchema,
        IServiceProvider? serviceProvider,
        Action<object> ownResource,
        IConfiglueHostPaths hostPaths
    )
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        if (IsEmpty)
        {
            return null;
        }

        return _sources.Build<TFragment>(modelSchema, serviceProvider, ownResource, hostPaths);
    }

    internal void Seal() => _sources.Seal();
}
