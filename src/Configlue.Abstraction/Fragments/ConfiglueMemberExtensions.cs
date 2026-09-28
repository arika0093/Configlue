namespace Configlue;

/// <summary>
/// Presence-preserving mutation helpers for generated fragment builders and source-local patches.
/// </summary>
/// <remarks>
/// A generated <c>FragmentBuilder</c> or <c>Patch</c> exposes each member as a
/// <see langword="ref"/> property, so these <see langword="ref"/> extension methods can update that
/// member in place. They are intended for advanced operations such as schema migration and edits to
/// one specific source. Ordinary editing should use natural C# on the model type itself; a presence
/// proxy is not the everyday editing surface and does not make generated members behave exactly
/// like the model type (for example, an implicit conversion cannot dispatch an instance method).
/// </remarks>
public static class ConfiglueMemberExtensions
{
    /// <summary>Removes this member from the source contribution without changing other members.</summary>
    /// <typeparam name="T">The member value type.</typeparam>
    /// <param name="member">The generated member to remove.</param>
    public static void Unset<T>(this ref Optional<T> member) => member = Optional<T>.Missing;

    /// <summary>Marks this source-local patch operation as removing the member contribution.</summary>
    /// <typeparam name="T">The member value type.</typeparam>
    /// <param name="operation">The generated patch operation to change.</param>
    public static void Unset<T>(this ref FragmentOperation<T> operation) =>
        operation = FragmentOperation<T>.Unset;

    /// <summary>Sets this member to a present value, including a present null or default value.</summary>
    /// <typeparam name="T">The member value type.</typeparam>
    /// <param name="member">The generated member to set.</param>
    /// <param name="value">The value to contribute.</param>
    public static void Set<T>(this ref Optional<T> member, T? value) =>
        member = Optional<T>.Present(value);

    /// <summary>Marks this source-local patch operation as setting a present value.</summary>
    /// <typeparam name="T">The member value type.</typeparam>
    /// <param name="operation">The generated patch operation to change.</param>
    /// <param name="value">The value to set, including an explicit null or default.</param>
    public static void Set<T>(this ref FragmentOperation<T> operation, T? value) =>
        operation = FragmentOperation<T>.Set(value);

    /// <summary>Copies the presence and value of another generated member of the same type.</summary>
    /// <typeparam name="T">The member value type.</typeparam>
    /// <param name="member">The generated member to assign.</param>
    /// <param name="source">The member whose presence and value are copied.</param>
    public static void CopyFrom<T>(this ref Optional<T> member, Optional<T> source) =>
        member = source;

    /// <summary>Copies presence from another generated member, converting a present value.</summary>
    /// <typeparam name="TSource">The source member value type.</typeparam>
    /// <typeparam name="TDestination">The destination member value type.</typeparam>
    /// <param name="member">The generated member to assign.</param>
    /// <param name="source">The member whose presence is copied.</param>
    /// <param name="convert">Converts a present source value to the destination value type.</param>
    public static void CopyFrom<TSource, TDestination>(
        this ref Optional<TDestination> member,
        Optional<TSource> source,
        Func<TSource, TDestination> convert
    )
    {
        ArgumentNullException.ThrowIfNull(convert);
        member = source.IsPresent
            ? Optional<TDestination>.Present(convert(source.Value!))
            : Optional<TDestination>.Missing;
    }

    /// <summary>
    /// Copies presence from a generated member into this patch operation: a present source sets the
    /// value, while a missing source removes the member contribution.
    /// </summary>
    /// <typeparam name="T">The member value type.</typeparam>
    /// <param name="operation">The generated patch operation to change.</param>
    /// <param name="source">The member whose presence and value are copied.</param>
    public static void CopyFrom<T>(this ref FragmentOperation<T> operation, Optional<T> source) =>
        operation = source.IsPresent
            ? FragmentOperation<T>.Set(source.Value)
            : FragmentOperation<T>.Unset;

    /// <summary>
    /// Copies presence into this patch operation while converting a present source value. A missing
    /// source marks the destination member as unset.
    /// </summary>
    /// <typeparam name="TSource">The source member value type.</typeparam>
    /// <typeparam name="TDestination">The destination member value type.</typeparam>
    /// <param name="operation">The generated patch operation to change.</param>
    /// <param name="source">The member whose presence and value are copied.</param>
    /// <param name="convert">Converts a present source value to the destination value type.</param>
    public static void CopyFrom<TSource, TDestination>(
        this ref FragmentOperation<TDestination> operation,
        Optional<TSource> source,
        Func<TSource, TDestination> convert
    )
    {
        ArgumentNullException.ThrowIfNull(convert);
        operation = source.IsPresent
            ? FragmentOperation<TDestination>.Set(convert(source.Value!))
            : FragmentOperation<TDestination>.Unset;
    }
}
