using Configlue;

namespace Configlue.Source.Environment;

/// <summary>Creates read-only state sources from prefixed process environment variables.</summary>
public static class EnvironmentStateSource
{
    /// <summary>
    /// Creates a sparse fragment source from variables such as <c>APP__DATABASE__HOST</c>.
    /// Member names are matched without regard to case. The source has no writer or watcher.
    /// </summary>
    public static StateSource<TFragment> FromEnvironment<TModel, TFragment>(
        string id,
        string prefix,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound,
        Func<IEnumerable<KeyValuePair<string, string?>>>? environmentVariables = null,
        Func<string, Type, object?>? valueParser = null
    )
        where TModel : IConfiglueModel<TModel, TFragment>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        var reader = new EnvironmentStateReader<TFragment>(
            TModel.ConfiglueSchema,
            prefix,
            environmentVariables,
            valueParser
        );
        return new StateSource<TFragment>(
            id,
            reader,
            priority,
            fallbackCondition,
            physicalOrigin: $"environment:{EnvironmentStateReader<TFragment>.NormalizePrefix(prefix)}"
        );
    }
}
