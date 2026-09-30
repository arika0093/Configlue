using Configlue;
using Configlue.CompilerServices;
using Configlue.Sources;

internal static class BenchmarkContextFactory
{
    public static ConfiglueContext Create<TModel, TFragment>(
        StateSourceSet<TFragment> sources,
        Action<ConfiglueModelBuilder<TModel>>? configure = null
    )
        where TModel : IConfiglueFacadeModel<TModel>
        where TFragment : class, IConfiglueFragment<TFragment>
    {
        ArgumentNullException.ThrowIfNull(sources);
        return ConfiglueApp.CreateContext(builder =>
            builder.Add<TModel>(model =>
            {
                configure?.Invoke(model);
                model.ConfigureSources(registration =>
                {
                    foreach (var source in sources.Sources)
                    {
                        registration.Sources.Add(source);
                    }
                });
            })
        );
    }
}
