namespace Configlue;

internal sealed record ConfiglueNamedOptionsProfile<TModel>(string Name)
{
    public Type ModelType => typeof(TModel);
}
