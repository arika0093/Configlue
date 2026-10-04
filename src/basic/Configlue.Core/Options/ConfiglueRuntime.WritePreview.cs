using Configlue.CompilerServices;

namespace Configlue;

internal sealed partial class ConfiglueRuntime<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    /// <inheritdoc />
    public async ValueTask<StateWritePreview> PreviewWriteAsync(
        TModel desired,
        CancellationToken cancellationToken = default
    )
    {
        using var operation = EnterOperation();
        ArgumentNullException.ThrowIfNull(desired);
        cancellationToken.ThrowIfCancellationRequested();

        var baseline = await ResolveCoreAsync(null, cancellationToken, captureContributions: true)
            .ConfigureAwait(false);
        if (baseline.Result.Status != StateReadStatus.Success || baseline.Result.Value is null)
        {
            throw new InvalidOperationException(
                $"Configuration state could not be read: {baseline.Result.Status}."
            );
        }

        var changes = Diff(baseline.Result.Value, desired);
        if (changes.IsEmpty)
        {
            return StateWritePreview.Empty;
        }

        var routedChanges = PartitionRoutedChanges(
            ModelSchema,
            changes,
            desired,
            ConfiglueMemberPath.Root(ModelSchema),
            _writePlan
        );
        var patches = CreateRoutedPatches(routedChanges, desired, baseline.Contributions);
        if (patches.Length == 0)
        {
            return StateWritePreview.Empty;
        }

        using var prepared = await PrepareWriteGroupsAsync(
                patches,
                baseline.Result.Revisions,
                desired,
                cancellationToken,
                baseline
            )
            .ConfigureAwait(false);
        return new StateWritePreview(prepared.Groups.Count, isEmpty: false);
    }

    private async ValueTask<StateWritePreview> PreviewForSubjectAsync(
        IConfiglueSubject subject,
        TModel desired,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(subject);
        using var scope = EnterSubject(subject);
        return await PreviewWriteAsync(desired, cancellationToken).ConfigureAwait(false);
    }
}
