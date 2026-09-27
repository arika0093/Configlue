namespace Configlue;

internal sealed record ConfiglueNamedOptionsProfile<TModel>
{
    public string Name { get; init; }

    public ConfiglueNamedOptionsProfile(string Name)
    {
        this.Name = Name;
    }

    public void Deconstruct(out string Name)
    {
        Name = this.Name;
    }

    public Type ModelType => typeof(TModel);
}
