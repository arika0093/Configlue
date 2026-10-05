using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// Immutable values flowing through one migration operation.
///
/// These records carry only the data of a single migration run (selected sources,
/// projected targets, revisions). They never hold runtime collaborators, so planning
/// stays separable from I/O and retirement side effects.
/// </summary>
internal readonly record struct MigrationSourceContribution<TModel, TFragment>(
    StateSource<TFragment> Source,
    StateReadResult<TFragment> Result,
    TFragment Fragment
)
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>;

/// <summary>Pure projection of the merged source fragment onto one migration target.</summary>
internal readonly record struct MigrationTargetPlan<TModel, TFragment>(
    StateSource<TFragment> Target,
    ISourceWriter<TFragment> Writer,
    TFragment Desired
)
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>;

/// <summary>Target pre-read bound to an optional same-resource base revision.</summary>
internal readonly record struct MigrationTargetCurrent<TModel, TFragment>(
    StateReadResult<TFragment> Result,
    TFragment Fragment,
    string? WriteBaseRevision
)
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>;
