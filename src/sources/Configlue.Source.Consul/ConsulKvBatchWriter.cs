using System.Text;
using System.Text.Json;

#pragma warning disable S3267 // Loops validate batch payloads and throw; LINQ would obscure failures.

namespace Configlue.Source.Consul;

internal sealed class ConsulKvBatchWriter<TFragment>
    : IResourceBatchWriter,
        IResourceBatchCompatibility
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly ConsulKvSource<TFragment> _source;
    private readonly JsonSerializerOptions _jsonOptions = new();

    public ConsulKvBatchWriter(ConsulKvSource<TFragment> source)
    {
        _source = source;
    }

    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        _source.GetResourceId(context);

    public object? GetBatchCompatibilityToken(ConfiglueResourceContext context) =>
        _source.GetBatchCompatibilityToken(context);

    public ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("Consul batch writes must use WriteBatchAsync.");

    public async ValueTask<StateWriteResult> WriteBatchAsync(
        IReadOnlyList<ResourceWriteMutation> mutations,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mutations);
        ResourceWriteMutation.ValidateBatch(mutations);
        if (mutations.Count == 0)
        {
            throw new ArgumentException("At least one mutation is required.", nameof(mutations));
        }

        var payloads = new List<ConsulBatchPayload>(mutations.Count);
        foreach (var mutation in mutations)
        {
            var bytes = mutation.Apply(ResourceReadResult.NotFound());
            payloads.Add(DecodePayload(bytes));
        }

        var first = payloads[0];
        if (
            payloads.Any(payload =>
                !string.Equals(payload.Datacenter, first.Datacenter, StringComparison.Ordinal)
                || !string.Equals(payload.Namespace, first.Namespace, StringComparison.Ordinal)
                || !string.Equals(payload.Partition, first.Partition, StringComparison.Ordinal)
            )
        )
        {
            throw new NotSupportedException(
                "Consul transaction batches cannot span datacenters, namespaces, or partitions."
            );
        }

        var operations = payloads.SelectMany(payload => payload.Operations).ToArray();
        if (operations.Length == 0)
        {
            var single = await _source
                .ResolveClient(mutations[0].Context.Route)
                .ListAsync(first.EffectivePrefix, ListOptionsFor(first), cancellationToken)
                .ConfigureAwait(false);
            return new StateWriteResult(ConsulKeyNormalization.FormatRevision(single.ConsulIndex));
        }

        if (operations.Length > 64)
        {
            throw new NotSupportedException("Consul transactions support at most 64 operations.");
        }

        var client = _source.ResolveClient(mutations[0].Context.Route);
        var result = await client
            .TransactAsync(operations, WriteOptionsFor(first), cancellationToken)
            .ConfigureAwait(false);
        if (!result.Committed)
        {
            var detail = result.Errors is { Length: > 0 }
                ? string.Join("; ", result.Errors)
                : "transaction rejected";
            throw new StateConflictException($"The Consul transaction was rejected ({detail}).");
        }

        return new StateWriteResult(ConsulKeyNormalization.FormatRevision(result.ConsulIndex));
    }

    public async ValueTask<StateWriteResult> WriteSingleAsync(
        ConsulKvSource<TFragment>.ConsulWritePlan plan,
        CancellationToken cancellationToken
    )
    {
        var client = _source.ResolveClient(plan.Context.Route);
        if (plan.Operations.Count == 0)
        {
            var current = await client
                .ListAsync(plan.EffectivePrefix, ListOptionsFor(plan), cancellationToken)
                .ConfigureAwait(false);
            return new StateWriteResult(ConsulKeyNormalization.FormatRevision(current.ConsulIndex));
        }

        if (plan.UseTransaction)
        {
            if (plan.Operations.Count > 64)
            {
                throw new NotSupportedException(
                    "Consul transactions support at most 64 operations."
                );
            }

            var result = await client
                .TransactAsync(plan.Operations, plan.WriteOptions, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Committed)
            {
                var detail = result.Errors is { Length: > 0 }
                    ? string.Join("; ", result.Errors)
                    : "transaction rejected";
                throw new StateConflictException(
                    $"The Consul transaction was rejected ({detail})."
                );
            }

            return new StateWriteResult(ConsulKeyNormalization.FormatRevision(result.ConsulIndex));
        }

        ulong lastIndex = 0;
        foreach (var operation in plan.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(operation.Verb, "delete", StringComparison.OrdinalIgnoreCase))
            {
                var deleted = await client
                    .DeleteAsync(operation.Key, operation.Cas, plan.WriteOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (!deleted.Applied)
                {
                    throw new StateConflictException(
                        $"The Consul key '{operation.Key}' changed after it was read."
                    );
                }

                lastIndex = deleted.NewIndex;
            }
            else
            {
                var put = await client
                    .PutAsync(
                        operation.Key,
                        operation.Value ?? Array.Empty<byte>(),
                        operation.Cas,
                        plan.WriteOptions,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (!put.Applied)
                {
                    throw new StateConflictException(
                        $"The Consul key '{operation.Key}' changed after it was read."
                    );
                }

                lastIndex = put.NewIndex;
            }
        }

        return new StateWriteResult(ConsulKeyNormalization.FormatRevision(lastIndex));
    }

    internal ReadOnlyMemory<byte> EncodePayload(ConsulKvSource<TFragment>.ConsulWritePlan plan)
    {
        var payload = new ConsulBatchPayload(
            plan.EffectivePrefix,
            plan.WriteOptions.Datacenter,
            plan.WriteOptions.Namespace,
            plan.WriteOptions.Partition,
            plan.Operations
        );
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, _jsonOptions));
    }

    private ConsulBatchPayload DecodePayload(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<ConsulBatchPayload>(
                    Encoding.UTF8.GetString(bytes.Span),
                    _jsonOptions
                ) ?? throw new InvalidOperationException("A Consul batch mutation was malformed.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "A Consul batch mutation was malformed.",
                exception
            );
        }
    }

    private static ConsulKvListOptions ListOptionsFor(ConsulBatchPayload payload) =>
        new()
        {
            Datacenter = payload.Datacenter,
            Namespace = payload.Namespace,
            Partition = payload.Partition,
        };

    private static ConsulKvListOptions ListOptionsFor(
        ConsulKvSource<TFragment>.ConsulWritePlan plan
    ) =>
        new()
        {
            Datacenter = plan.WriteOptions.Datacenter,
            Namespace = plan.WriteOptions.Namespace,
            Partition = plan.WriteOptions.Partition,
        };

    private static ConsulKvWriteOptions WriteOptionsFor(ConsulBatchPayload payload) =>
        new()
        {
            Datacenter = payload.Datacenter,
            Namespace = payload.Namespace,
            Partition = payload.Partition,
        };

    private sealed record ConsulBatchPayload(
        string EffectivePrefix,
        string? Datacenter,
        string? Namespace,
        string? Partition,
        IReadOnlyList<ConsulTxnOperation> Operations
    );
}
