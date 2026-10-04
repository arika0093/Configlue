namespace Configlue.Source.Http;

/// <summary>The observed state returned by a State HTTP PATCH request.</summary>
/// <typeparam name="TFragment">The generated sparse fragment type.</typeparam>
/// <param name="Value">The canonical state observed after the patch, when the server returned one.</param>
/// <param name="Revision">The new effective-state revision (ETag hex), when the server returned one.</param>
public sealed record HttpStatePatchResult<TFragment>(TFragment? Value, string? Revision)
    where TFragment : class, IConfiglueFragment<TFragment>;
