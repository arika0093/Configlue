using Configlue.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Hosting.AspNetCore;

/// <summary>Write preview/atomicity and commit stage (Core edit sessions).</summary>
internal static class StateEndpointWrite
{
    public sealed record StateServices<TModel>(
        IReadOnlyState<TModel> ReadState,
        IConfiglueEditSessions<TModel> EditSessions
    )
        where TModel : IConfiglueFacadeModel<TModel>;

    /// <summary>Resolves read/edit services. Null when a 500 was written.</summary>
    public static async Task<StateServices<TModel>?> ResolveServicesAsync<TModel>(
        HttpContext context
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        try
        {
            var readState = context.RequestServices.GetRequiredService<IReadOnlyState<TModel>>();
            var editSessions = context.RequestServices.GetRequiredService<
                IConfiglueEditSessions<TModel>
            >();
            return new StateServices<TModel>(readState, editSessions);
        }
        catch (InvalidOperationException exception)
        {
            await StateEndpointProblems
                .WriteServicesProblemAsync(context, exception)
                .ConfigureAwait(false);
            return null;
        }
    }

    public sealed record AtomicPreview(bool IsEmpty, bool IsAtomic, int PhysicalWriteCount);

    /// <summary>
    /// RFC 5789 atomicity gate: determines executability before the first physical write.
    /// Returns null when a problem was written. Empty previews are no-ops.
    /// </summary>
    public static async Task<AtomicPreview?> PreviewWriteAsync<TModel>(
        HttpContext context,
        IConfiglueEditSessions<TModel> editSessions,
        TModel desired
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        if (editSessions is not IConfiglueWritePreview<TModel> preview)
        {
            await StateEndpointProblems
                .WriteProblemAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "State services are not registered.",
                    "The state does not expose an atomic PATCH write preview."
                )
                .ConfigureAwait(false);
            return null;
        }

        StateWritePreview? writePreview;
        try
        {
            writePreview = await preview
                .PreviewWriteAsync(desired, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception
                    is ConfiglueValidationException
                        or StateConflictException
                        or NotSupportedException
                        or InvalidOperationException
            )
        {
            await StateEndpointProblems
                .WritePreviewProblemAsync(context, exception)
                .ConfigureAwait(false);
            return null;
        }

        if (!writePreview.IsAtomic)
        {
            await StateEndpointProblems
                .WriteNonAtomicProblemAsync(context, writePreview.PhysicalWriteCount)
                .ConfigureAwait(false);
            return null;
        }

        return new AtomicPreview(
            writePreview.IsEmpty,
            writePreview.IsAtomic,
            writePreview.PhysicalWriteCount
        );
    }

    /// <summary>Commits the desired model through an edit session. Null when mapped.</summary>
    public static async Task<TModel?> CommitDesiredAsync<TModel>(
        HttpContext context,
        IConfiglueEditSessions<TModel> editSessions,
        TModel desired
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        try
        {
            using var session = await editSessions
                .OpenEditSessionAsync(context.RequestAborted)
                .ConfigureAwait(false);
            session.Value = desired;
            await session.CommitAsync(context.RequestAborted).ConfigureAwait(false);
            return session.Value;
        }
        catch (Exception exception)
            when (exception
                    is ConfiglueValidationException
                        or StateConflictException
                        or StateMultiWriteException
                        or InvalidOperationException
            )
        {
            await StateEndpointProblems
                .WriteCommitProblemAsync(context, exception)
                .ConfigureAwait(false);
            return default;
        }
    }
}
