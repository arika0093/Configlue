namespace Configlue.Resources;

/// <summary>A deferred change to physical resource content used by a single-resource batch write.</summary>
public sealed class ResourceWriteMutation
{
    private readonly Func<ResourceReadResult, ReadOnlyMemory<byte>> _apply;
    private readonly ReadOnlyMemory<byte>? _ownedReplacementContent;

    /// <summary>Creates a resource mutation.</summary>
    public ResourceWriteMutation(
        RevisionCondition condition,
        StateSchemaMetadata? schema,
        Func<ResourceReadResult, ReadOnlyMemory<byte>> apply,
        string? scope = null,
        bool canCompose = false,
        ConfiglueResourceContext context = default
    )
        : this(condition, schema, apply, scope, canCompose, null, NormalizeContext(context)) { }

    private ResourceWriteMutation(
        RevisionCondition condition,
        StateSchemaMetadata? schema,
        Func<ResourceReadResult, ReadOnlyMemory<byte>> apply,
        string? scope,
        bool canCompose,
        ReadOnlyMemory<byte>? ownedReplacementContent,
        ConfiglueResourceContext context
    )
    {
        ArgumentNullException.ThrowIfNull(apply);
        if (canCompose && string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException(
                "A composable resource mutation must declare its content scope.",
                nameof(scope)
            );
        }

        if (schema is { IsValid: false })
        {
            throw new ArgumentException(
                "A resource mutation schema must have a positive schema version.",
                nameof(schema)
            );
        }

        Condition = condition;
        Schema = schema;
        _apply = apply;
        Scope = scope;
        CanCompose = canCompose;
        _ownedReplacementContent = ownedReplacementContent;
        Context = context;
    }

    /// <summary>The explicit concurrency precondition for this mutation.</summary>
    public RevisionCondition Condition { get; }

    /// <summary>
    /// The physical schema metadata declared by this mutation, or <see langword="null"/> when the
    /// mutation makes no claim about the persisted container schema. A null value leaves existing
    /// container metadata unchanged; it never means "clear".
    /// </summary>
    public StateSchemaMetadata? Schema { get; }

    /// <summary>The provider-defined slash-delimited scope modified by this operation.</summary>
    public string? Scope { get; }

    /// <summary>Whether this mutation can safely compose with other disjoint scoped mutations.</summary>
    public bool CanCompose { get; }

    /// <summary>The logical subject and source-specific key for this mutation.</summary>
    public ConfiglueResourceContext Context { get; }

    internal bool HasStableContent { get; private set; }

    /// <summary>Applies the mutation to the current physical resource content.</summary>
    public ReadOnlyMemory<byte> Apply(ResourceReadResult current) => _apply(current);

    internal bool TryGetOwnedReplacementContent(out ReadOnlyMemory<byte> content)
    {
        if (_ownedReplacementContent.HasValue)
        {
            content = _ownedReplacementContent.Value;
            return true;
        }

        content = default;
        return false;
    }

    /// <summary>Creates a full-resource replacement mutation for one subject.</summary>
    public static ResourceWriteMutation Replace(
        ResourceWriteRequest request,
        ConfiglueResourceContext context
    )
    {
        var content = request.ContentIsOwned ? request.Content : request.Content.ToArray();
        var mutation = new ResourceWriteMutation(
            request.Condition,
            request.Schema,
            _ => content,
            scope: null,
            canCompose: false,
            ownedReplacementContent: content,
            ConfiglueResourceContext.Normalize(context)
        );
        mutation.HasStableContent = true;
        return mutation;
    }

    /// <summary>Returns this mutation with the logical subject and key for its resource operation.</summary>
    public ResourceWriteMutation WithContext(ConfiglueResourceContext context) =>
        new(
            Condition,
            Schema,
            _apply,
            Scope,
            CanCompose,
            _ownedReplacementContent,
            NormalizeContext(context)
        )
        {
            HasStableContent = HasStableContent,
        };

    private static ConfiglueResourceContext NormalizeContext(ConfiglueResourceContext context) =>
        ConfiglueResourceContext.Normalize(context);

    /// <summary>
    /// Resolves the single physical container schema declared by a batch of mutations.
    /// Mutations without schema metadata are treated as making no claim, so existing container
    /// metadata is preserved. Declaring two different non-null schemas for one physical write is
    /// rejected before any content is written.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Two mutations declare different physical container schemas.
    /// </exception>
    public static StateSchemaMetadata? ResolveBatchSchema(
        IReadOnlyList<ResourceWriteMutation> mutations
    )
    {
        ArgumentNullException.ThrowIfNull(mutations);
        StateSchemaMetadata? resolved = null;
        for (var index = 0; index < mutations.Count; index++)
        {
            var mutation =
                mutations[index]
                ?? throw new ArgumentException(
                    "A mutation batch cannot contain null values.",
                    nameof(mutations)
                );
            if (mutation.Schema is not { } schema)
            {
                continue;
            }

            if (resolved is not { } existing)
            {
                resolved = schema;
                continue;
            }

            if (existing != schema)
            {
                throw new NotSupportedException(
                    $"Resource mutations declare conflicting container schema metadata "
                        + $"'{existing.ModelId ?? "<none>"}' v{existing.Version} and "
                        + $"'{schema.ModelId ?? "<none>"}' v{schema.Version}."
                );
            }
        }

        return resolved;
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
            throw new ArgumentException(
                "A mutation batch cannot contain null values.",
                nameof(mutations)
            );
        }

        var condition = mutations[0].Condition;
        if (mutations.Any(mutation => mutation.Condition != condition))
        {
            throw new StateConflictException(
                "Mutations for one resource were prepared from different revisions."
            );
        }

        _ = ResolveBatchSchema(mutations);

        if (mutations.Count == 1)
        {
            return;
        }

        if (mutations.Any(static mutation => !mutation.CanCompose || mutation.Scope is null))
        {
            throw new NotSupportedException(
                "Full-resource replacements cannot be combined with other resource mutations."
            );
        }

        var mutationDomain = mutations[0].Scope!.Split('/')[0];
        if (
            mutations
                .Skip(1)
                .Any(mutation =>
                    !string.Equals(
                        mutation.Scope!.Split('/')[0],
                        mutationDomain,
                        StringComparison.Ordinal
                    )
                )
        )
        {
            throw new NotSupportedException(
                "Resource mutations from different provider domains cannot be combined."
            );
        }

        for (var leftIndex = 0; leftIndex < mutations.Count; leftIndex++)
        {
            var left = mutations[leftIndex].Scope!;
            for (var rightIndex = leftIndex + 1; rightIndex < mutations.Count; rightIndex++)
            {
                var right = mutations[rightIndex].Scope!;
                if (
                    string.Equals(left, right, StringComparison.Ordinal)
                    || right.StartsWith(left + "/", StringComparison.Ordinal)
                    || left.StartsWith(right + "/", StringComparison.Ordinal)
                )
                {
                    throw new StateConflictException(
                        $"Resource mutation scopes '{left}' and '{right}' overlap."
                    );
                }
            }
        }
    }
}
