using Configlue.CompilerServices;

namespace Configlue;

/// <summary>One source contribution to a resolved state.</summary>
internal readonly record struct ResolvedContribution<TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    public StateSource<TFragment> Source { get; init; }
    public StateReadResult<TFragment> Result { get; init; }
    public bool IsModelDefaults { get; init; }
    public ConfiglueResourceContext? ResourceContext { get; init; }
    public ResourceId? ResourceId { get; init; }

    public ResolvedContribution(
        StateSource<TFragment> Source,
        StateReadResult<TFragment> Result,
        bool IsModelDefaults = false,
        ConfiglueResourceContext? ResourceContext = null,
        ResourceId? ResourceId = null
    )
    {
        this.Source = Source;
        this.Result = Result;
        this.IsModelDefaults = IsModelDefaults;
        this.ResourceContext = ResourceContext;
        this.ResourceId = ResourceId;
    }

    public void Deconstruct(
        out StateSource<TFragment> Source,
        out StateReadResult<TFragment> Result,
        out bool IsModelDefaults
    )
    {
        Source = this.Source;
        Result = this.Result;
        IsModelDefaults = this.IsModelDefaults;
    }
}

/// <summary>One non-contributing source read observed during resolution.</summary>
internal readonly record struct ResolvedFailure<TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    public StateSource<TFragment> Source { get; init; }
    public StateReadResult<TFragment> Result { get; init; }
    public ConfiglueResourceContext? ResourceContext { get; init; }
    public ResourceId? ResourceId { get; init; }

    public ResolvedFailure(
        StateSource<TFragment> Source,
        StateReadResult<TFragment> Result,
        ConfiglueResourceContext? ResourceContext = null,
        ResourceId? ResourceId = null
    )
    {
        this.Source = Source;
        this.Result = Result;
        this.ResourceContext = ResourceContext;
        this.ResourceId = ResourceId;
    }

    public void Deconstruct(
        out StateSource<TFragment> Source,
        out StateReadResult<TFragment> Result
    )
    {
        Source = this.Source;
        Result = this.Result;
    }
}

/// <summary>Per-source probe emitted while resolving, used by diagnostics check.</summary>
internal readonly record struct ResolvedSourceProbe<TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    public StateSource<TFragment> Source { get; init; }
    public StateReadResult<TFragment> Result { get; init; }
    public bool Contributed { get; init; }
    public bool FallbackContinued { get; init; }
    public ConfiglueResourceContext? ResourceContext { get; init; }
    public ResourceId? ResourceId { get; init; }
    public Exception? Exception { get; init; }
}

/// <summary>Full resolution: the read result plus its merge inputs.</summary>
internal readonly record struct ResolvedState<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    public StateReadResult<TModel> Result { get; init; }
    public IReadOnlyList<ResolvedContribution<TFragment>> Contributions { get; init; }
    public TFragment? MergedFragment { get; init; }
    public IReadOnlyList<ResolvedFailure<TFragment>> Failures { get; init; }

    public ResolvedState(
        StateReadResult<TModel> Result,
        IReadOnlyList<ResolvedContribution<TFragment>> Contributions,
        TFragment? MergedFragment,
        IReadOnlyList<ResolvedFailure<TFragment>> Failures
    )
    {
        this.Result = Result;
        this.Contributions = Contributions;
        this.MergedFragment = MergedFragment;
        this.Failures = Failures;
    }

    public void Deconstruct(
        out StateReadResult<TModel> Result,
        out IReadOnlyList<ResolvedContribution<TFragment>> Contributions,
        out TFragment? MergedFragment,
        out IReadOnlyList<ResolvedFailure<TFragment>> Failures
    )
    {
        Result = this.Result;
        Contributions = this.Contributions;
        MergedFragment = this.MergedFragment;
        Failures = this.Failures;
    }
}
