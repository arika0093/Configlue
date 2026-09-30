namespace Configlue;

/// <summary>A generated set/unset patch for one configuration model schema.</summary>
public interface IConfigluePatch
{
    /// <summary>The model schema this patch targets.</summary>
    ConfiglueModelSchema Schema { get; }

    /// <summary>Whether this patch contains no operations.</summary>
    bool IsEmpty { get; }

    /// <summary>Applies this patch to a source-local fragment.</summary>
    IConfiglueFragment Apply(IConfiglueFragment fragment);
}
