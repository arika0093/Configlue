namespace Configlue.Extensibility;

/// <summary>Creates a generated-model source using the provider registration context.</summary>
public interface IConfiglueSourceDefinition
{
    /// <summary>Creates a source and declares the resources created for its lifetime.</summary>
    ConfiglueSourceCreation<TFragment> Create<TFragment>(ConfiglueSourceCreationContext context)
        where TFragment : class, IConfiglueFragment<TFragment>;
}
