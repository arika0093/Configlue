using System.Text.Json;
using Configlue.CompilerServices;

namespace Configlue.Sources;

/// <summary>One normalized flat assignment accepted by the shared schema-aware binder.</summary>
/// <remarks>
/// <c>PathSegments</c> are raw member-name segments (for example split on <c>__</c> or
/// <c>.</c>); resolution is case-insensitive. <c>RawValue</c> is the transport value
/// (text for environment variables, parsed objects for command-line symbols).
/// <c>Origin</c> names the transport key (environment key or symbol name) for
/// diagnostics and for revision inputs that resolve to no member.
/// </remarks>
internal readonly record struct TextAssignment(
    string[] PathSegments,
    object? RawValue,
    string Origin
);

/// <summary>How the shared binder resolves several assignments targeting one member.</summary>
internal enum TextAssignmentDuplicatePolicy
{
    /// <summary>Reject duplicates.</summary>
    Throw,

    /// <summary>Later assignments win.</summary>
    LastWins,
}

/// <summary>Hooks customizing the shared text-assignment binder per transport.</summary>
internal sealed class TextAssignmentBinderOptions
{
    /// <summary>
    /// Optional text parser hook (for example environment scalar overrides).
    /// Invoked only for string raw values. Throw <see cref="NotSupportedException"/>
    /// to decline and fall back to the shared scalar/JSON conversion.
    /// </summary>
    public Func<string, Type, object?>? TextParser { get; init; }

    /// <summary>JSON options for structured members bound from text.</summary>
    public JsonSerializerOptions? JsonOptions { get; init; }

    /// <summary>How duplicate canonical targets are resolved.</summary>
    public TextAssignmentDuplicatePolicy DuplicatePolicy { get; init; } =
        TextAssignmentDuplicatePolicy.Throw;
}

/// <summary>Result of binding flat assignments into a sparse fragment.</summary>
/// <remarks>
/// <c>Revision</c> follows the deterministic binder revision specified on
/// <see cref="TextAssignmentBinder"/>:
/// converted (not raw) values for matched members plus raw values for unmatched
/// origins. Two binds with equal logical content share a revision even when
/// their transports differ (text versus typed scalars, JSON list versus typed
/// array) or their input order differs.
/// </remarks>
internal readonly record struct BoundTextFragment(
    IConfiglueFragment Fragment,
    bool MatchedAny,
    string Revision
);

/// <summary>
/// Shared schema-aware binder for flat key/value transports (environment, command line).
/// Owns member-path resolution, nested fragment construction, scalar/JSON/collection
/// conversion, duplicate handling, and deterministic revision construction.
/// Transports own naming/symbol extraction only; custom parsers are hooks here.
/// Supports only Configlue's documented model shapes.
/// </summary>
/// <remarks>
/// Transport contract: transports pass raw member-name segments without resolving
/// them (environment splits prefixed keys on "__" and maps explicit names;
/// command line passes registration-validated mapping paths) and select a
/// duplicate policy (environment rejects duplicates, command line lets later
/// mappings win). The binder owns canonical case-insensitive resolution,
/// conversion, deduplication by canonical path, and revision, so transports
/// must not pre-resolve or pre-deduplicate.
/// <para>
/// Conversion rules (intentional unification of the legacy transports):
/// values already assignable to the target type pass through untouched;
/// string values for scalar targets use invariant-culture scalar parsing while
/// strings for collection/object targets fall back to JSON deserialization.
/// The JSON fallback is new for command-line transports (which previously
/// rejected such text); it is intentional so both transports share one matrix.
/// All conversion failures throw <see cref="FormatException"/> shaped
/// "The value for model path '{path}' from '{origin}' ..." regardless of
/// transport, replacing the legacy "Environment variable ..." and
/// "The command-line value ..." prefixes.
/// </para>
/// <para>
/// Unknown path segments are unmatched rather than errors: binding ignores them
/// but folds their origins into the revision, and a fully unmatched bind yields
/// <c>MatchedAny == false</c> (readers surface that as NotFound). This matches
/// the environment transport's legacy behavior; for command line it is
/// unreachable via the public API because mapping paths are validated at
/// registration, so the legacy throw for unknown segments was dead code.
/// </para>
/// </remarks>
internal static partial class TextAssignmentBinder
{
    /// <summary>Binds flat assignments into a sparse fragment.</summary>
    /// <remarks>
    /// Revision is a deterministic SHA256 (uppercase hex) over length-prefixed
    /// entries: matched entries as (canonical dotted path,
    /// rendered converted value) sorted with ordinal comparison, then unmatched
    /// entries as (origin, rendered raw value) sorted by origin.
    /// The converted-value basis is an intentional spec change from the legacy
    /// transports (environment hashed upper-cased keys with raw text; command
    /// line hashed raw parsed values): text "8" and typed 8 bound to one member
    /// now share a revision, and concrete collection identity is erased
    /// (a JSON list and a typed array with equal elements share a revision).
    /// Unmatched origins are included so callers observe underlying changes
    /// even when nothing binds. Input order does not affect the revision.
    /// </remarks>
    public static BoundTextFragment Bind(
        ConfiglueModelSchema schema,
        IReadOnlyList<TextAssignment> assignments,
        TextAssignmentBinderOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(assignments);
        options ??= new TextAssignmentBinderOptions();

        var lookups = new Dictionary<ConfiglueModelSchema, TextAssignmentMemberLookup>();
        CollectLookups(schema, new HashSet<Type>(), lookups);

        // Resolve to canonical paths; unknown members are unmatched (ignored for
        // binding but included in the revision so callers observe underlying changes).
        var resolved = new List<ResolvedAssignment>(assignments.Count);
        var unmatched = new List<TextAssignment>();
        foreach (var assignment in assignments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(assignment.PathSegments);
            ArgumentException.ThrowIfNullOrWhiteSpace(assignment.Origin);
            if (assignment.PathSegments.Length == 0)
            {
                unmatched.Add(assignment);
                continue;
            }

            var canonical = ResolveCanonicalPath(
                schema,
                assignment.PathSegments,
                assignment.Origin,
                lookups
            );
            if (canonical is null)
            {
                unmatched.Add(assignment);
                continue;
            }

            resolved.Add(new ResolvedAssignment(canonical, assignment.RawValue, assignment.Origin));
        }

        // Deduplicate by canonical path.
        var deduped = new Dictionary<string, ResolvedAssignment>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var item in resolved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = string.Join(".", item.CanonicalPath);
            if (deduped.TryGetValue(key, out var existing))
            {
                if (options.DuplicatePolicy == TextAssignmentDuplicatePolicy.Throw)
                {
                    throw new InvalidOperationException(
                        $"More than one value maps to model property '{key}' ('{existing.Origin}' and '{item.Origin}')."
                    );
                }

                deduped[key] = item;
            }
            else
            {
                deduped.Add(key, item);
                order.Add(key);
            }
        }

        order.Sort(StringComparer.Ordinal);

        IConfiglueFragment fragment = schema.CreateEmptyFragment();
        var matchedAny = false;
        var convertedForRevision = new List<KeyValuePair<string, object?>>(order.Count);
        foreach (var key in order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = deduped[key];
            var applied = SetValue(
                fragment,
                schema,
                item.CanonicalPath,
                0,
                item.RawValue,
                item.Origin,
                options,
                lookups,
                cancellationToken
            );
            fragment = applied.Fragment;
            if (applied.Matched)
            {
                matchedAny = true;
                convertedForRevision.Add(
                    new KeyValuePair<string, object?>(key, applied.ConvertedValue)
                );
            }
            else
            {
                unmatched.Add(new TextAssignment(item.CanonicalPath, item.RawValue, item.Origin));
            }
        }

        var revision = CreateRevision(convertedForRevision, unmatched, cancellationToken);
        return new BoundTextFragment(fragment, matchedAny, revision);
    }

    private static void CollectLookups(
        ConfiglueModelSchema schema,
        HashSet<Type> ancestors,
        Dictionary<ConfiglueModelSchema, TextAssignmentMemberLookup> lookups
    )
    {
        if (!ancestors.Add(schema.ModelType))
        {
            return;
        }

        lookups[schema] = TextAssignmentMemberLookup.Create(schema);
        for (var index = 0; index < schema.Members.Count; index++)
        {
            var nestedFactory = schema.Members[index].NestedSchemaFactory;
            if (nestedFactory is not null)
            {
                CollectLookups(nestedFactory(), ancestors, lookups);
            }
        }

        ancestors.Remove(schema.ModelType);
    }

    private static string[]? ResolveCanonicalPath(
        ConfiglueModelSchema schema,
        string[] path,
        string origin,
        Dictionary<ConfiglueModelSchema, TextAssignmentMemberLookup> lookups
    )
    {
        var canonical = new string[path.Length];
        var current = schema;
        for (var index = 0; index < path.Length; index++)
        {
            if (!lookups.TryGetValue(current, out var lookup))
            {
                lookup = TextAssignmentMemberLookup.Create(current);
                lookups[current] = lookup;
            }

            if (!lookup.TryResolve(path[index], out var member, out var isAmbiguous))
            {
                if (isAmbiguous)
                {
                    throw new FormatException(
                        $"Path segment '{path[index]}' from '{origin}' is ambiguous in schema '{current.Id}'."
                    );
                }

                return null;
            }

            canonical[index] = member.Name;
            if (index == path.Length - 1)
            {
                return canonical;
            }

            if (member.NestedSchemaFactory is null)
            {
                throw new FormatException(
                    $"Path '{string.Join(".", path)}' from '{origin}' continues past non-nested member '{member.Name}'."
                );
            }

            current = member.NestedSchemaFactory();
        }

        return null;
    }

    private sealed record ResolvedAssignment(
        string[] CanonicalPath,
        object? RawValue,
        string Origin
    );

    private readonly record struct AppliedFragment(
        IConfiglueFragment Fragment,
        bool Matched,
        object? ConvertedValue
    );

    private static AppliedFragment SetValue(
        IConfiglueFragment fragment,
        ConfiglueModelSchema schema,
        string[] path,
        int pathIndex,
        object? rawValue,
        string origin,
        TextAssignmentBinderOptions options,
        Dictionary<ConfiglueModelSchema, TextAssignmentMemberLookup> lookups,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!lookups[schema].TryResolve(path[pathIndex], out var member, out var isAmbiguous))
        {
            if (isAmbiguous)
            {
                throw new FormatException(
                    $"Path segment '{path[pathIndex]}' from '{origin}' is ambiguous in schema '{schema.Id}'."
                );
            }

            return new AppliedFragment(fragment, false, null);
        }

        if (pathIndex == path.Length - 1)
        {
            if (member.NestedSchemaFactory is not null)
            {
                throw new FormatException(
                    $"The value from '{origin}' names nested model '{member.Name}'. Use additional segments to set its members."
                );
            }

            var converted = ConvertValue(
                rawValue,
                member.ValueType,
                string.Join(".", path),
                origin,
                options
            );
            return new AppliedFragment(fragment.WithMember(member.Id, converted), true, converted);
        }

        if (member.NestedSchemaFactory is null)
        {
            throw new FormatException(
                $"Path '{string.Join(".", path)}' from '{origin}' continues past non-nested member '{member.Name}'."
            );
        }

        var nestedSchema = member.NestedSchemaFactory();
        var nestedFragment =
            FindPresentMember(fragment, member.Id) as IConfiglueFragment
            ?? nestedSchema.CreateEmptyFragment();
        var nested = SetValue(
            nestedFragment,
            nestedSchema,
            path,
            pathIndex + 1,
            rawValue,
            origin,
            options,
            lookups,
            cancellationToken
        );
        return nested.Matched
            ? new AppliedFragment(
                fragment.WithMember(member.Id, nested.Fragment),
                true,
                nested.ConvertedValue
            )
            : new AppliedFragment(fragment, false, null);
    }

    private static object? FindPresentMember(IConfiglueFragment fragment, int memberId)
    {
        foreach (var member in fragment.EnumeratePresentMembers())
        {
            if (member.Id == memberId)
            {
                return member.Value;
            }
        }

        return null;
    }
}
