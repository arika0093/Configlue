namespace Configlue;

/// <summary>A deferred change to physical resource content used by a single-resource batch write.</summary>
public sealed class ResourceWriteMutation
{
    private readonly Func<ResourceReadResult, ReadOnlyMemory<byte>> _apply;

    /// <summary>Creates a resource mutation.</summary>
    public ResourceWriteMutation(
        string? expectedRevision,
        bool checkRevision,
        StateSchemaMetadata? schema,
        Func<ResourceReadResult, ReadOnlyMemory<byte>> apply,
        string? scope = null,
        bool canCompose = false)
    {
        ArgumentNullException.ThrowIfNull(apply);
        if (canCompose && string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("A composable resource mutation must declare its content scope.", nameof(scope));
        }

        ExpectedRevision = expectedRevision;
        CheckRevision = checkRevision;
        Schema = schema;
        _apply = apply;
        Scope = scope;
        CanCompose = canCompose;
    }

    /// <summary>The revision on which this mutation is based.</summary>
    public string? ExpectedRevision { get; }

    /// <summary>Whether the expected revision must be checked even when it is null.</summary>
    public bool CheckRevision { get; }

    /// <summary>The schema metadata, if this mutation replaces a typed resource.</summary>
    public StateSchemaMetadata? Schema { get; }

    /// <summary>The provider-defined slash-delimited scope modified by this operation.</summary>
    public string? Scope { get; }

    /// <summary>Whether this mutation can safely compose with other disjoint scoped mutations.</summary>
    public bool CanCompose { get; }

    /// <summary>Applies the mutation to the current physical resource content.</summary>
    public ReadOnlyMemory<byte> Apply(ResourceReadResult current) => _apply(current);

    /// <summary>Creates a full-resource replacement mutation.</summary>
    public static ResourceWriteMutation Replace(ResourceWriteRequest request)
    {
        var content = request.Content.ToArray();
        return new ResourceWriteMutation(
            request.ExpectedRevision,
            request.CheckRevision,
            request.Schema,
            _ => content,
            canCompose: false);
    }

    /// <summary>Validates that a set of mutations can be applied in one physical write.</summary>
    public static void ValidateBatch(IReadOnlyList<ResourceWriteMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        if (mutations.Count == 0)
        {
            throw new ArgumentException("At least one mutation is required.", nameof(mutations));
        }

        if (mutations.Any(static mutation => mutation is null))
        {
            throw new ArgumentException("A mutation batch cannot contain null values.", nameof(mutations));
        }

        var expectedRevision = mutations[0].ExpectedRevision;
        if (mutations.Any(mutation => !string.Equals(mutation.ExpectedRevision, expectedRevision, StringComparison.Ordinal)))
        {
            throw new StateConflictException("Mutations for one resource were prepared from different revisions.");
        }

        if (mutations.Count == 1)
        {
            return;
        }

        if (mutations.Any(static mutation => !mutation.CanCompose || mutation.Scope is null))
        {
            throw new NotSupportedException("Full-resource replacements cannot be combined with other resource mutations.");
        }

        var mutationDomain = mutations[0].Scope!.Split('/')[0];
        if (mutations.Skip(1).Any(mutation => !string.Equals(mutation.Scope!.Split('/')[0], mutationDomain, StringComparison.Ordinal)))
        {
            throw new NotSupportedException("Resource mutations from different provider domains cannot be combined.");
        }

        for (var leftIndex = 0; leftIndex < mutations.Count; leftIndex++)
        {
            var left = mutations[leftIndex].Scope!;
            for (var rightIndex = leftIndex + 1; rightIndex < mutations.Count; rightIndex++)
            {
                var right = mutations[rightIndex].Scope!;
                if (string.Equals(left, right, StringComparison.Ordinal) ||
                    right.StartsWith(left + "/", StringComparison.Ordinal) ||
                    left.StartsWith(right + "/", StringComparison.Ordinal))
                {
                    throw new StateConflictException($"Resource mutation scopes '{left}' and '{right}' overlap.");
                }
            }
        }
    }
}
