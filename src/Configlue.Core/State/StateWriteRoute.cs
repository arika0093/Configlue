namespace Configlue;

/// <summary>Selects the source that receives an ordinary state write.</summary>
public readonly record struct StateWriteRoute
{
    /// <summary>Gets or initializes the <see cref="SourceId"/> value.</summary>
    public string? SourceId { get; init; }

    /// <summary>Initializes a new instance of this record.</summary>
    /// <param name="SourceId">The initial value for the <see cref="SourceId"/> property.</param>
    public StateWriteRoute(string? SourceId = null)
    {
        this.SourceId = SourceId;
    }

    /// <summary>Deconstructs this record into its property values.</summary>
    /// <param name="SourceId">Receives the current <see cref="SourceId"/> value.</param>
    public void Deconstruct(out string? SourceId)
    {
        SourceId = this.SourceId;
    }

    /// <summary>Uses the highest-priority source that has a writer.</summary>
    public static StateWriteRoute HighestPriorityWritable => default;

    /// <summary>Selects a source by its logical identifier.</summary>
    public static StateWriteRoute To(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        return new StateWriteRoute(sourceId);
    }

    /// <summary>Selects the source represented by a typed source key.</summary>
    public static StateWriteRoute To<TModel>(SourceKey<TModel> sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey.Id))
        {
            throw new ArgumentException("The source key is uninitialized.", nameof(sourceKey));
        }

        return new StateWriteRoute(sourceKey.Id);
    }
}
