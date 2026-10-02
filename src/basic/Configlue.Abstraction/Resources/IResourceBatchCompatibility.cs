namespace Configlue.Resources;

/// <summary>
/// Opt-in contract for batch writers that can prove they are interchangeable with another writer that
/// reports the same <see cref="ResourceId"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ResourceId"/> identifies the physical coordination and atomicity domain of a resource. It
/// does not, by itself, prove that every writer reporting that identity is semantically interchangeable:
/// wrappers such as <c>TransformingResource</c>, section views, and serializing adapters carry behavior
/// (transformation, serialization, transaction handling) in addition to physical identity.
/// </para>
/// <para>
/// The runtime only combines several writers into one physical batch write when they are the same object,
/// or when every writer opts in by returning equal, non-null compatibility tokens for the operation
/// context. Writers that do not implement this interface, or that return <see langword="null"/>, can only
/// batch with themselves. Compatibility is validated before any physical write is attempted.
/// </para>
/// </remarks>
public interface IResourceBatchCompatibility
{
    /// <summary>
    /// Gets a token that identifies this writer's batch-writer semantics for one operation context.
    /// Two distinct writers can share one physical batch write only when both return equal, non-null
    /// tokens. Returning <see langword="null"/> means only reference equality establishes compatibility.
    /// </summary>
    /// <param name="context">The logical subject and source-specific key for the batch operation.</param>
    object? GetBatchCompatibilityToken(ConfiglueResourceContext context);
}

/// <summary>Compares batch writers for compatibility using the <see cref="IResourceBatchCompatibility"/> contract.</summary>
public static class ResourceBatchCompatibility
{
    /// <summary>
    /// Determines whether two batch writers can contribute disjoint mutations to one physical batch write.
    /// </summary>
    /// <param name="left">The first batch writer.</param>
    /// <param name="right">The second batch writer.</param>
    /// <param name="context">The logical subject and source-specific key for the batch operation.</param>
    /// <returns>
    /// <see langword="true"/> when the writers are the same object, or when both opt in to an equal,
    /// non-null compatibility token for <paramref name="context"/>.
    /// </returns>
    public static bool AreCompatible(
        IResourceBatchWriter left,
        IResourceBatchWriter right,
        ConfiglueResourceContext context = default
    )
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (
            left is IResourceBatchCompatibility leftCompatibility
            && right is IResourceBatchCompatibility rightCompatibility
        )
        {
            var leftToken = leftCompatibility.GetBatchCompatibilityToken(context);
            var rightToken = rightCompatibility.GetBatchCompatibilityToken(context);
            return leftToken is not null && Equals(leftToken, rightToken);
        }

        return false;
    }
}
