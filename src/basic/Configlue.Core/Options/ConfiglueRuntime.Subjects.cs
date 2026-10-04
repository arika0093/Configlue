using Configlue.CompilerServices;

namespace Configlue;

internal sealed partial class ConfiglueRuntime<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private sealed class SubjectBoundOptions(
        ConfiglueRuntime<TModel, TFragment> owner,
        IConfiglueSubject subject
    )
        : IWritableState<TModel>,
            IConfiglueDetailsRuntime,
            IConfiglueStateSnapshotRuntime<TModel>,
            IConfiglueDiagnostics<TModel>,
            IConfiglueEditSessions<TModel>,
            IConfiglueWritePreview<TModel>
    {
        public IDisposable OnChange(Action<TModel> listener) =>
            owner._watches.WatchSubject(subject, listener);

        public ConfiglueStateDiagnostics GetDiagnostics() => owner.GetDiagnostics();

        public ValueTask<TModel> GetValueAsync(CancellationToken cancellationToken = default) =>
            owner.GetValueForSubjectAsync(subject, cancellationToken);

        public ValueTask<StateWriteReceipt> SaveAsync(
            IConfiglueModelPatch<TModel> patch,
            CancellationToken cancellationToken = default
        ) => owner.SaveForSubjectAsync(subject, patch, cancellationToken);

        public ConfiglueCheckOperation Check(CancellationToken cancellationToken = default) =>
            owner.CreateCheckOperation(subject, cancellationToken);

        public ValueTask<EditSession<TModel>> OpenEditSessionAsync(
            CancellationToken cancellationToken = default
        ) => owner.OpenEditSessionForSubjectAsync(subject, null, cancellationToken);

        public ValueTask<EditSession<TModel>> OpenEditSessionAsync(
            StateWritePlan writePlan,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(writePlan);
            return owner.OpenEditSessionForSubjectAsync(subject, writePlan, cancellationToken);
        }

        public ValueTask<StateWritePreview> PreviewWriteAsync(
            TModel desired,
            CancellationToken cancellationToken = default
        ) => owner._writes.PreviewForSubjectAsync(subject, desired, cancellationToken);

        async ValueTask<ConfiglueDetailsSnapshot> IConfiglueDetailsRuntime.GetDetailsSnapshotAsync(
            CancellationToken cancellationToken
        )
        {
            using var scope = owner._subjects.Enter(subject);
            return await ((IConfiglueDetailsRuntime)owner)
                .GetDetailsSnapshotAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        async ValueTask<
            StateSnapshot<TModel>
        > IConfiglueStateSnapshotRuntime<TModel>.GetSnapshotAsync(
            CancellationToken cancellationToken
        )
        {
            using var scope = owner._subjects.Enter(subject);
            return await ((IConfiglueStateSnapshotRuntime<TModel>)owner)
                .GetSnapshotAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
