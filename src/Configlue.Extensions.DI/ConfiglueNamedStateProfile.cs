namespace Configlue;

internal sealed record ConfiglueNamedStateProfile<TModel>
{
    public string Name { get; init; }

    public ConfiglueNamedStateProfile(string Name)
    {
        this.Name = Name;
    }

    public void Deconstruct(out string Name)
    {
        Name = this.Name;
    }

    public Type ModelType => typeof(TModel);
}
