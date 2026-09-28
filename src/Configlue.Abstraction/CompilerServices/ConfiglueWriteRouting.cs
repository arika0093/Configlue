namespace Configlue.CompilerServices;

/// <summary>The generated path routing bridge; no string parsing occurs during route lookup.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class ConfiglueWriteRouting
{
    /// <summary>Binds diagnostic routes to the generated root schema once.</summary>
    public static StateWritePlan Bind(StateWritePlan plan, ConfiglueModelSchema schema) =>
        plan.Bind(schema);

    /// <summary>Resolves the most specific generated path owner.</summary>
    public static string? Resolve(
        StateWritePlan plan,
        ConfiglueMemberPath path,
        string? fallbackSourceId = null
    ) => plan.ResolveSourceIdOrNull(path, fallbackSourceId);

    /// <summary>Checks for a more specific generated path owner.</summary>
    public static bool HasRouteBelow(StateWritePlan plan, ConfiglueMemberPath path) =>
        plan.HasRouteBelow(path);
}
