using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Configlue.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Configlue;

internal sealed partial class ConfiglueRuntime<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private async ValueTask<TFragment> MigrateAsync(
        TFragment value,
        StateSchemaMetadata sourceSchema,
        CancellationToken cancellationToken,
        string? sourceId = null,
        long parentOperationId = 0
    )
    {
        var diagnostic = _diagnostics.Start(
            ConfiglueDiagnosticEventKind.MigrationStarted,
            sourceId,
            parentOperationId
        );
        try
        {
            var result = await _migrationChain
                .MigrateAsync(value, sourceSchema, cancellationToken)
                .ConfigureAwait(false);
            diagnostic.Complete(ConfiglueDiagnosticEventKind.MigrationCompleted);
            return result;
        }
        catch (Exception exception)
        {
            diagnostic.Fail(
                ConfiglueDiagnosticEventKind.MigrationFailed,
                exception,
                exception is OperationCanceledException && cancellationToken.IsCancellationRequested
            );
            throw;
        }
    }
}
